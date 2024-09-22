using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using WebApplicationTest1.Data;
using WebApplicationTest1.Models;
using BCrypt.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using WebApplicationTest1;

var builder = WebApplication.CreateBuilder(args);
var key = Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:Secret"]);

void ConfigureServices(IServiceCollection services)
{
    services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes
            (JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
    });
}


// Создаем словарь для хранения активных поездок
var activeTrips = new Dictionary<int, CancellationTokenSource>();


builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

// Добавляем регистрацию TripWorker
builder.Services.AddScoped<TripWorker>();

// Add services

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddHttpClient();
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Please enter token",
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        BearerFormat = "JWT",
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Configure middleware
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/api/protected", [Authorize] () =>
{
    return "Hello, protected API!";
}).RequireAuthorization();

app.MapPost("/auth/register", async ([FromBody] AuthRequest authRequest, AppDbContext db) =>
{
    if (await db.Users.AnyAsync(u => u.Username == authRequest.Username))
    {
        return Results.BadRequest("User with this username already exists.");
    }

    var passwordHash = BCrypt.Net.BCrypt.HashPassword(authRequest.Password);

    var newUser = new User
    {
        Name = authRequest.Name,
        Username = authRequest.Username,
        PasswordHash = passwordHash,
        CityId = authRequest.CityId
    };

    db.Users.Add(newUser);
    await db.SaveChangesAsync();

    return Results.Ok("User registered successfully.");
});

// Метод входа
app.MapPost("/auth/login", async ([FromBody] AuthRequest authRequest, AppDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == authRequest.Username);
    if (user == null || !BCrypt.Net.BCrypt.Verify(authRequest.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var token = GenerateJwtToken(user);
    Console.WriteLine($"Generated Token: {token}");  // Логирование токена
    return Results.Ok(new { Token = token });
});

// Метод для генерации JWT токена
string GenerateJwtToken(User user)
{
    var tokenHandler = new JwtSecurityTokenHandler();
    var key = Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:Secret"]);  // убедитесь, что ключ загружается правильно
    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString())
        }),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
    };

    var token = tokenHandler.CreateToken(tokenDescriptor);
    return tokenHandler.WriteToken(token);
}

app.MapPost("/users", async ([FromBody]User user, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(user.Name) || user.Name.Length <= 2)
    {
        return Results.BadRequest("Name must be longer than 2 characters.");
    }

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/users/{user.UserId}", user);
}).RequireAuthorization();

// Create a city
app.MapPost("/cities", async ([FromBody] City city, AppDbContext db) =>
{
    if (city.Latitude < -90 || city.Latitude > 90 || city.Longitude < -180 || city.Longitude > 180)
    {
        return Results.BadRequest("Invalid coordinates.");
    }

    db.Cities.Add(city);
    await db.SaveChangesAsync();

    return Results.Created($"/cities/{city.CityId}", city);
}).RequireAuthorization();

// Get user by id with weather
app.MapGet("/users/{id:int}", async (int id, AppDbContext db, HttpClient httpClient) =>
{
    var user = await db.Users.Include(u => u.City).FirstOrDefaultAsync(u => u.UserId == id);
    if (user == null)
    {
        return Results.NotFound();
    }
    return Results.Ok(new
    {
        user.UserId,
        user.Name,
        City = user.City.Name,
        Weather = GetWeather(user.City.Latitude, user.City.Longitude, httpClient)
    });
});

// Get users within radius
app.MapGet("/users/lat={lat}&lon={lon}&r={r}", async (double lat, double lon, double r, AppDbContext db) =>
{
    var users = await db.Users.Include(u => u.City).ToListAsync();
    var usersInRange = users.Where(u =>
        Distance(lat, lon, u.City.Latitude, u.City.Longitude) <= r);

    return Results.Ok(usersInRange);
});

// Создание поездки
// Регистрация маршрутов
app.MapPost("/trips", async ([FromBody] TripRequest tripRequest, AppDbContext db, TripWorker tripWorker) =>
{
    var user = await db.Users.FindAsync(tripRequest.UserId);
    if (user == null)
    {
        return Results.NotFound("User not found.");
    }

    // Проверка на активный переезд
    if (tripWorker.IsTripActive(tripRequest.UserId, activeTrips))
    {
        return Results.BadRequest("User is already on a trip.");
    }

    // Создаем новую поездку
    var newTrip = new Trip
    {
        UserId = tripRequest.UserId,
        DestinationCityId = tripRequest.DestinationCityId,
        StartTime = DateTime.UtcNow,
        EndTime = DateTime.UtcNow.AddSeconds(tripRequest.TripTime),
        Status = TripStatus.InProgress,
        CancellationToken = Guid.NewGuid().ToString()
    };

    db.Trips.Add(newTrip);
    await db.SaveChangesAsync();

    // Запускаем фоновый воркер для отслеживания поездки
    _ = Task.Run(() => tripWorker.StartTrip(newTrip.TripId, newTrip.UserId, TimeSpan.FromSeconds(tripRequest.TripTime), activeTrips));

    return Results.Ok(new { TripId = newTrip.TripId, CancellationToken = newTrip.CancellationToken });
});

app.MapPost("/trips/cancel", async ([FromQuery] string cancellationToken, AppDbContext db, TripWorker tripWorker) =>
{
    var trip = await db.Trips.FirstOrDefaultAsync(t => t.CancellationToken == cancellationToken && t.Status == TripStatus.InProgress);
    if (trip == null)
    {
        return Results.NotFound("No active trip found with the provided token.");
    }

    await tripWorker.CancelTrip(trip.TripId, activeTrips);

    return Results.Ok("Trip cancelled.");
});


app.MapGet("/trips/{tripId}", async (int tripId, AppDbContext db) =>
{
    var trip = await db.Trips.FindAsync(tripId);
    if (trip == null)
    {
        return Results.NotFound();
    }

    var status = trip.Status switch
    {
        TripStatus.InProgress => $"In Progress, {trip.EndTime.Subtract(DateTime.UtcNow).TotalSeconds} seconds left.",
        TripStatus.Cancelled => "Cancelled",
        TripStatus.Completed => "Completed",
        _ => "Unknown status"
    };

    return Results.Ok(new { TripId = trip.TripId, Status = status });
});

async Task<Weather> GetWeather(double lat, double lon, HttpClient httpClient)
{
    var weatherUrl =
        $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid=d16145386361c174778ea49e5058b789&units=metric";
    var weatherResponse = await httpClient.GetAsync(weatherUrl);
    if (weatherResponse.IsSuccessStatusCode)
    {
        var json = await weatherResponse.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var weather = new Weather
        {
            Description = result.GetProperty("weather")[0].GetProperty("description").GetString(),
            Temperature = result.GetProperty("main").GetProperty("temp").GetDouble()
        };
        return weather;
    }
    return new Weather();
}

// Calculate distance between two coordinates
double Distance(double lat1, double lon1, double lat2, double lon2)
{
    var R = 6371e3; // Earth's radius in meters
    var φ1 = lat1 * Math.PI / 180;
    var φ2 = lat2 * Math.PI / 180;
    var Δφ = (lat2 - lat1) * Math.PI / 180;
    var Δλ = (lon2 - lon1) * Math.PI / 180;

    var a = Math.Sin(Δφ / 2) * Math.Sin(Δφ / 2) +
            Math.Cos(φ1) * Math.Cos(φ2) *
            Math.Sin(Δλ / 2) * Math.Sin(Δλ / 2);

    var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

    return R * c / 1000; // Convert to kilometers
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.Cities.Any())
    {
        var cities = Enumerable.Range(1, 50).Select(i => new City
        {
            Name = $"City {i}",
            Latitude = new Random().NextDouble() * 180 - 90,
            Longitude = new Random().NextDouble() * 360 - 180
        }).ToList();

        db.Cities.AddRange(cities);
        db.SaveChanges();
        
        var users = Enumerable.Range(1, 50).Select(i => new User
        {
            Name = $"User {i}",
            Username = $"UserName {i}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("111"),
            CityId = cities[new Random().Next(0, 49)].CityId
        }).ToList();

        db.Users.AddRange(users);
        db.SaveChanges();
    }
}

app.Run();

public class AuthRequest
{
    public string Name { get; set; } // Для регистрации
    public string Username { get; set; }
    public string Password { get; set; }
    public int CityId { get; set; } // Для регистрации
}

public class Weather
{
    public string? Description { get; set; }
    public double Temperature { get; set; }
}

public class TripRequest
{
    public int UserId { get; set; }
    public int DestinationCityId { get; set; }
    public int TripTime { get; set; } // Время поездки в секундах
}
