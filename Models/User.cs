using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplicationTest1.Models;

public class User
{
    [Key]
    public int UserId { get; set; }

    [Required, MinLength(3)]
    public string Name { get; set; }

    [MinLength(3)]
    public string Username { get; set; }

    public string PasswordHash { get; set; }

    [ForeignKey("City")]
    public int CityId { get; set; }

    public City City { get; set; }
}