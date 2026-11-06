using System.Security.Claims;
using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Controllers;

[Authorize]
public class MediaController : Controller
{
    private static readonly string[] AllowedTypes = { "image/jpeg", "image/png", "image/tiff" };
    private readonly StorageService _storage;
    private readonly AppDbContext _db;
    public MediaController(StorageService storage, AppDbContext db) { _storage = storage; _db = db; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Fields = await _db.Devices.Select(d => d.FieldId).Distinct().OrderBy(f => f).ToListAsync();
        try
        {
            await _storage.EnsureCreatedAsync();
            ViewBag.Audit = await _storage.LatestAuditAsync(15);
            return View(await _storage.ListImagesAsync());
        }
        catch (Exception ex) when (ex is AggregateException or Azure.RequestFailedException)
        {
            ViewBag.Audit = new List<Azure.Data.Tables.TableEntity>();
            TempData["Error"] = "Storage is unavailable. Start Azurite (blob 10000, table 10002) and refresh.";
            return View(new List<MediaItem>());
        }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> Upload(string fieldId, IFormFile? file)
    {
        if (file == null || file.Length == 0) TempData["Error"] = "Choose an image file.";
        else if (!AllowedTypes.Contains(file.ContentType)) TempData["Error"] = "Only JPEG, PNG or TIFF images are allowed.";
        else if (!await _db.Devices.AnyAsync(d => d.FieldId == fieldId)) TempData["Error"] = "Unknown field.";
        else
        {
            var name = await _storage.UploadImageAsync(fieldId, file, User.Identity!.Name!);
            TempData["Success"] = $"Uploaded {name}. The blob-triggered function will log it shortly.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Media/File/{*name}")]
    public async Task<IActionResult> File(string name)
    {
        var r = await _storage.OpenImageAsync(name);
        return r == null ? NotFound() : File(r.Value.Content, r.Value.ContentType);
    }
}
