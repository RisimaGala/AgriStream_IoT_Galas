using System.Security.Claims;
using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly AppDbContext _db;
    private readonly FunctionsClient _functions;
    private readonly StorageService _storage;
    private readonly ILogger<DashboardController> _log;

    public DashboardController(AppDbContext db, FunctionsClient functions, StorageService storage, ILogger<DashboardController> log)
    {
        _db = db; _functions = functions; _storage = storage; _log = log;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Farmers can only see devices they own; agronomists and admins can see everything.</summary>
    private IQueryable<Device> VisibleDevices() =>
        User.IsInRole(Roles.Farmer) ? _db.Devices.Where(d => d.OwnerUserId == UserId) : _db.Devices;

    public async Task<IActionResult> Index(string? field)
    {
        var devices = await VisibleDevices().AsNoTracking().OrderBy(d => d.DeviceId).ToListAsync();
        var fields = devices.Select(d => d.FieldId).Distinct().OrderBy(f => f).ToList();
        field = fields.Contains(field ?? "") ? field : fields.FirstOrDefault();
        return View(new DashboardViewModel
        {
            Fields = fields,
            SelectedField = field,
            Devices = devices.Where(d => d.FieldId == field).ToList()
        });
    }

    /// <summary>AJAX endpoint polled by the dashboard; proxies the GET Azure Function.</summary>
    [HttpGet]
    public async Task<IActionResult> Telemetry(string field)
    {
        if (!await VisibleDevices().AnyAsync(d => d.FieldId == field)) return Forbid();
        try
        {
            return Content(await _functions.GetLatestTelemetryAsync(field, 20), "application/json");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telemetry function call failed for {Field}; reading Table Storage directly", field);
            try { return Json(await _storage.LatestReadingsAsync(field, 20)); }
            catch (Exception ex2)
            {
                _log.LogError(ex2, "Telemetry fallback failed for {Field}", field);
                return StatusCode(502, new { error = "Telemetry service unavailable" });
            }
        }
    }

    /// <summary>Device status from the SQL master registry (and is updated by the queue-triggered function).</summary>
    [HttpGet]
    public async Task<IActionResult> DeviceStatus(string field)
    {
        var list = await VisibleDevices().AsNoTracking().Where(d => d.FieldId == field)
            .Select(d => new { d.DeviceId, d.Name, d.Status, d.LastCommand, d.LastCommandAtUtc })
            .ToListAsync();
        return Json(list);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Farmer,Agronomist,Admin")]
    public async Task<IActionResult> TriggerIrrigation(string deviceId, string action = "START", int minutes = 15)
    {
        var dev = await VisibleDevices().AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (dev == null) return NotFound();
        action = action.ToUpperInvariant() == "STOP" ? "STOP" : "START";
        minutes = Math.Clamp(minutes, 1, 120);

        bool ok; string body;
        try
        {
            (ok, body) = await _functions.TriggerIrrigationAsync(
                new IrrigationRequest(dev.DeviceId, dev.FieldId, action, minutes, User.Identity!.Name!, "UserButton"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogError(ex, "Irrigation function unreachable");
            (ok, body) = (false, "the Azure Functions app is not running (start AgriStream.Functions on port 7071).");
        }
        TempData[ok ? "Success" : "Error"] = ok
            ? $"Irrigation command {action} queued for {dev.DeviceId}."
            : $"Could not queue command: {body}";
        return RedirectToAction(nameof(Index), new { field = dev.FieldId });
    }
}
