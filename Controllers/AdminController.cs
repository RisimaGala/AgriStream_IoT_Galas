using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Controllers;

[Authorize(Roles = Roles.Admin)]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly StorageService _storage;
    public AdminController(AppDbContext db, StorageService storage) { _db = db; _storage = storage; }

    public async Task<IActionResult> Users()
    {
        var devices = await _db.Devices.AsNoTracking().ToListAsync();
        ViewBag.Fields = devices.Select(d => d.FieldId).Distinct().OrderBy(f => f).ToList();
        // field -> ids of users who own at least one device in it
        ViewBag.FieldOwners = devices.GroupBy(d => d.FieldId)
            .ToDictionary(g => g.Key, g => g.Where(d => d.OwnerUserId != null).Select(d => d.OwnerUserId!.Value).Distinct().ToList());
        return View(await _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync());
    }

    /// <summary>Assigns whole fields to a farmer: ticked fields get their devices owned by the user, unticked ones are released.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetFields(int id, string[]? fields)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        fields ??= Array.Empty<string>();
        var devices = await _db.Devices.ToListAsync();
        foreach (var d in devices)
        {
            if (fields.Contains(d.FieldId)) d.OwnerUserId = u.Id;
            else if (d.OwnerUserId == u.Id) d.OwnerUserId = null;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = fields.Length == 0
            ? $"{u.Username} now has no fields."
            : $"{u.Username} can now see: {string.Join(", ", fields)}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, string role)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null || !Roles.All.Contains(role)) return BadRequest();
        if (u.Username == User.Identity!.Name && role != Roles.Admin)
        {
            TempData["Error"] = "You cannot remove your own admin role.";
            return RedirectToAction(nameof(Users));
        }
        u.Role = role;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{u.Username} is now {role}. (Takes effect at their next sign-in.)";
        return RedirectToAction(nameof(Users));
    }

    /// <summary>Generates readings for every of the registered device, standing in for the real sensor fleet.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SimulateTelemetry(int rounds = 5)
    {
        var rnd = new Random();
        var devices = await _db.Devices.AsNoTracking().ToListAsync();
        for (var i = 0; i < Math.Clamp(rounds, 1, 50); i++)
            foreach (var d in devices)
                await _storage.AddReadingAsync(d.FieldId, d.DeviceId,
                    Math.Round(20 + rnd.NextDouble() * 40, 1), Math.Round(14 + rnd.NextDouble() * 12, 1),
                    Math.Round(5.5 + rnd.NextDouble() * 2, 2), Math.Round(18 + rnd.NextDouble() * 18, 1),
                    Math.Round(30 + rnd.NextDouble() * 50, 1));
        TempData["Success"] = $"Wrote {rounds * devices.Count} simulated readings to Table Storage.";
        return RedirectToAction(nameof(Users));
    }
}
