using WebApplicationTest1.Data;
using WebApplicationTest1.Models;

namespace WebApplicationTest1;

public class TripWorker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TripWorker> _logger;

    public TripWorker(IServiceScopeFactory scopeFactory, ILogger<TripWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartTrip(int tripId, int userId, TimeSpan duration, Dictionary<int, CancellationTokenSource> activeTrips)
    {
        var cancellationTokenSource = new CancellationTokenSource();

        lock (activeTrips)
        {
            activeTrips[tripId] = cancellationTokenSource;
        }

        try
        {
            for (var timeLeft = duration; timeLeft.TotalSeconds > 0; timeLeft -= TimeSpan.FromSeconds(5))
            {
                if (cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _logger.LogInformation($"Trip {tripId} for user {userId} has been cancelled.");
                    await CancelTrip(tripId, activeTrips);
                    return;
                }

                _logger.LogInformation($"User {userId} is still on trip {tripId}. Time left: {timeLeft.TotalSeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationTokenSource.Token);
            }

            await CompleteTrip(tripId);
            _logger.LogInformation($"Trip {tripId} for user {userId} completed.");
        }
        finally
        {
            lock (activeTrips)
            {
                activeTrips.Remove(tripId);
            }
        }
    }

    public async Task CompleteTrip(int tripId)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trip = await dbContext.Trips.FindAsync(tripId);
            if (trip == null) return;

            trip.Status = TripStatus.Completed;
            var user = await dbContext.Users.FindAsync(trip.UserId);
            if (user != null)
            {
                user.CityId = trip.DestinationCityId;
            }

            await dbContext.SaveChangesAsync();
        }
    }

    public async Task CancelTrip(int tripId, Dictionary<int, CancellationTokenSource> activeTrips)
    {
        CancellationTokenSource cancellationTokenSource;
        lock (activeTrips)
        {
            if (!activeTrips.TryGetValue(tripId, out cancellationTokenSource))
            {
                _logger.LogInformation($"Trip {tripId} not found or already completed.");
                return;
            }

            activeTrips.Remove(tripId);
        }

        cancellationTokenSource.Cancel();

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trip = await dbContext.Trips.FindAsync(tripId);
            if (trip == null) return;

            trip.Status = TripStatus.Cancelled;
            await dbContext.SaveChangesAsync();
        }

        _logger.LogInformation($"Trip {tripId} has been cancelled.");
    }

    public bool IsTripActive(int tripId, Dictionary<int, CancellationTokenSource> activeTrips)
    {
        lock (activeTrips)
        {
            return activeTrips.ContainsKey(tripId);
        }
    }
}