using System.ComponentModel.DataAnnotations;

namespace AgriStream_IoT_Galas.Models;

public static class Roles
{
    public const string Farmer = "Farmer";
    public const string Agronomist = "Agronomist";
    public const string Admin = "Admin";
    public static readonly string[] All = { Farmer, Agronomist, Admin };
}

/// <summary>Simple user account: username, password hash and one assigned role.</summary>
public class AppUser
{
    public int Id { get; set; }
    [Required, StringLength(50)] public string Username { get; set; } = "";
    [Required] public string PasswordHash { get; set; } = "";
    [Required, StringLength(20)] public string Role { get; set; } = Roles.Farmer;
}

/// <summary>Master device registry (Azure SQL). Operational status is updated by the queue-triggered function.</summary>
public class Device
{
    [Key, StringLength(50)] public string DeviceId { get; set; } = "";
    [Required, StringLength(50)] public string FieldId { get; set; } = "";
    [StringLength(100)] public string Name { get; set; } = "";
    [StringLength(20)] public string Status { get; set; } = "Idle";
    [StringLength(20)] public string? LastCommand { get; set; }
    public DateTime? LastCommandAtUtc { get; set; }
    public int? OwnerUserId { get; set; }
}

public class LoginViewModel
{
    [Required] public string Username { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required, StringLength(50, MinimumLength = 3)] public string Username { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 6), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [Required] public string Role { get; set; } = Roles.Farmer;
}

public class DashboardViewModel
{
    public List<string> Fields { get; set; } = new();
    public string? SelectedField { get; set; }
    public List<Device> Devices { get; set; } = new();
}

public class MediaItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public string Field { get; set; } = "";
}
