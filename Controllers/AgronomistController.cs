using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Controllers;

[Authorize(Roles = "Agronomist,Admin")]
public class AgronomistController : Controller
{
    private static readonly string[] Categories = { "Pest", "Disease", "Weed", "Nutrient deficiency", "Other" };
    private static readonly string[] Severities = { "Low", "Medium", "High" };

    private readonly AppDbContext _db;
    private readonly AgronomyService _agro;
    private readonly ILogger<AgronomistController> _log;
    public AgronomistController(AppDbContext db, AgronomyService agro, ILogger<AgronomistController> log)
    { _db = db; _agro = agro; _log = log; }

    public async Task<IActionResult> Index(string? field)
    {
        var devices = await _db.Devices.AsNoTracking().OrderBy(d => d.DeviceId).ToListAsync();
        var fields = devices.Select(d => d.FieldId).Distinct().OrderBy(f => f).ToList();
        field = fields.Contains(field ?? "") ? field : fields.FirstOrDefault();
        var vm = new AgronomistViewModel { Fields = fields, SelectedField = field };
        if (field == null) return View(vm);

        vm.Devices = devices.Where(d => d.FieldId == field).ToList();
        try
        {
            vm.Analysis = await _agro.AnalyseAsync(field);
            vm.Harvests = await _agro.HarvestsAsync(field);
            vm.Notes = await _agro.NotesAsync(field);
            vm.YieldInsight = AgronomyService.YieldInsight(vm.Harvests);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Agronomist dashboard could not read storage");
            TempData["Error"] = "Storage is unavailable. Start Azurite and refresh.";
            vm.Analysis = new FieldAnalysis { Field = field };
        }
        ViewBag.Categories = Categories;
        ViewBag.Severities = Severities;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddHarvest(string field, string season, string crop, double areaHa, double yieldTonnes)
    {
        if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(crop) || areaHa <= 0 || yieldTonnes < 0)
            TempData["Error"] = "Enter a season, crop, area (> 0) and yield.";
        else
        {
            await _agro.AddHarvestAsync(new HarvestRecord
            {
                Field = field, Season = season.Trim(), Crop = crop.Trim(), AreaHa = areaHa, YieldTonnes = yieldTonnes,
                RecordedBy = User.Identity!.Name!
            });
            TempData["Success"] = $"Harvest record saved for {field} ({season.Trim()}, {crop.Trim()}).";
        }
        return RedirectToAction(nameof(Index), new { field });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(string field, string category, string severity, string note, string? advice)
    {
        if (string.IsNullOrWhiteSpace(note) || !Categories.Contains(category) || !Severities.Contains(severity))
            TempData["Error"] = "Describe what you observed.";
        else
        {
            await _agro.AddNoteAsync(new ScoutNote
            {
                Field = field, Category = category, Severity = severity, Note = note.Trim(),
                Advice = advice?.Trim() ?? "", RecordedBy = User.Identity!.Name!
            });
            TempData["Success"] = "Scouting note saved.";
        }
        return RedirectToAction(nameof(Index), new { field });
    }

    /// <summary>Backfills ~2 hours of simulated readings for this field so the analysis and charts have data to work with.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedReadings(string field, [FromServices] StorageService storage)
    {
        var devices = await _db.Devices.AsNoTracking().Where(d => d.FieldId == field).ToListAsync();
        if (devices.Count == 0) return NotFound();
        var rnd = new Random();
        var start = DateTime.UtcNow.AddMinutes(-120);
        double moisture = 32 + rnd.NextDouble() * 20;
        for (var i = 0; i < 24; i++)                       // one round every 5 minutes
        {
            moisture = Math.Clamp(moisture + (rnd.NextDouble() - 0.55) * 4, 18, 70);
            foreach (var d in devices)
                await storage.AddReadingAsync(field, d.DeviceId,
                    Math.Round(moisture + (rnd.NextDouble() - 0.5) * 3, 1), Math.Round(17 + rnd.NextDouble() * 8, 1),
                    Math.Round(6.1 + rnd.NextDouble() * 0.9, 2), Math.Round(20 + rnd.NextDouble() * 10, 1),
                    Math.Round(45 + rnd.NextDouble() * 30, 1), start.AddMinutes(i * 5).AddSeconds(devices.IndexOf(d)));
        }
        TempData["Success"] = $"Added {24 * devices.Count} simulated sensor readings for {field}.";
        return RedirectToAction(nameof(Index), new { field });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedSample(string field)
    {
        var fields = await _db.Devices.Select(d => d.FieldId).Distinct().ToListAsync();
        await _agro.SeedSampleHarvestsAsync(fields, User.Identity!.Name!);
        TempData["Success"] = "Sample harvest history loaded (labelled as sample data).";
        return RedirectToAction(nameof(Index), new { field });
    }
}
