using AgriStream_IoT_Galas.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Device> Devices => Set<Device>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();
    }
}

public static class DbSeeder
{
    public static void Seed(AppDbContext db, IConfiguration cfg)
    {
        db.Database.EnsureCreated();
        var hasher = new PasswordHasher<AppUser>();
        if (!db.Users.Any())
        {
            var adminPw = cfg["Seed:AdminPassword"] ?? "Admin#12345";
            foreach (var (name, role, pw) in new[]
            {
                ("admin", Roles.Admin, adminPw),
                ("farmer1", Roles.Farmer, "Farmer#12345"),
                ("agro1", Roles.Agronomist, "Agro#12345"),
            })
            {
                var u = new AppUser { Username = name, Role = role };
                u.PasswordHash = hasher.HashPassword(u, pw);
                db.Users.Add(u);
            }
            db.SaveChanges();
        }
        // The agronomist account (added separately so it also appears in databases that were seeded earlier).
        if (!db.Users.Any(u => u.Username == "OrlandoBarker"))
        {
            var orlando = new AppUser { Username = "OrlandoBarker", Role = Roles.Agronomist };
            orlando.PasswordHash = hasher.HashPassword(orlando, cfg["Seed:AgronomistPassword"] ?? "Orlando#12345");
            db.Users.Add(orlando);
            db.SaveChanges();
        }
        if (!db.Devices.Any())
        {
            var farmer = db.Users.First(u => u.Username == "farmer1").Id;
            for (var i = 1; i <= 6; i++)
                db.Devices.Add(new Device
                {
                    DeviceId = $"valve-{i:00}",
                    FieldId = i <= 3 ? "field-north" : "field-south",
                    Name = $"Irrigation valve {i}",
                    OwnerUserId = farmer
                });
            db.SaveChanges();
        }
    }
}
