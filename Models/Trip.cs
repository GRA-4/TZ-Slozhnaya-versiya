using System.ComponentModel.DataAnnotations;

namespace WebApplicationTest1.Models;

public class Trip
{
    [Key]
    public int TripId { get; set; }

    [Required]
    public int UserId { get; set; } // Внешний ключ на User
    public User User { get; set; }

    [Required]
    public int DestinationCityId { get; set; } // Внешний ключ на City
    public City DestinationCity { get; set; }

    [Required]
    public DateTime StartTime { get; set; }

    [Required]
    public DateTime EndTime { get; set; }

    [Required]
    public TripStatus Status { get; set; }

    public string CancellationToken { get; set; }
}

public enum TripStatus
{
    InProgress,
    Cancelled,
    Completed
}