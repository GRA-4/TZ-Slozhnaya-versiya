using System.ComponentModel.DataAnnotations;

namespace WebApplicationTest1.Models;

public class City
{
    [Key]
    public int CityId { get; set; }

    [Required]
    public string Name { get; set; }

    [Required]
    public double Latitude { get; set; }

    [Required]
    public double Longitude { get; set; }
}