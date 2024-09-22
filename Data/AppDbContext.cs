using Microsoft.EntityFrameworkCore;
using WebApplicationTest1.Models;

namespace WebApplicationTest1.Data;

public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<City> Cities { get; set; }
    public DbSet<Trip> Trips { get; set; } // Новый DbSet для таблицы Trip

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasOne(u => u.City)
            .WithMany()
            .HasForeignKey(u => u.CityId);

        modelBuilder.Entity<Trip>()
            .HasOne(t => t.User)
            .WithMany() // У одного пользователя может быть много поездок
            .HasForeignKey(t => t.UserId);

        modelBuilder.Entity<Trip>()
            .HasOne(t => t.DestinationCity)
            .WithMany() // У одного города могут быть многие поездки
            .HasForeignKey(t => t.DestinationCityId);
    }
}