using AgriStream_IoT_Galas.Models;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgriStream_IoT_Galas.Services;

/// <summary>Blob Storage (drone imagery) and Table Storage (telemetry ingest + audit read-back).</summary>
public class StorageService
{
    public const string ImageContainer = "drone-images";
    public const string TelemetryTable = "SensorReadings";
    public const string AuditTable = "ImageProcessingAudit";

    private readonly BlobContainerClient _container;
    private readonly TableClient _telemetry;
    private readonly TableClient _audit;

    public StorageService(IConfiguration cfg)
    {
        var cs = cfg["Storage:ConnectionString"] ?? "UseDevelopmentStorage=true";
        _container = new BlobServiceClient(cs).GetBlobContainerClient(ImageContainer);
        var tables = new TableServiceClient(cs);
        _telemetry = tables.GetTableClient(TelemetryTable);
        _audit = tables.GetTableClient(AuditTable);
    }

    private volatile bool _ensured;

    public async Task EnsureCreatedAsync()
    {
        if (_ensured) return;
        await _container.CreateIfNotExistsAsync();
        await _telemetry.CreateIfNotExistsAsync();
        await _audit.CreateIfNotExistsAsync();
        _ensured = true;
    }

    public async Task<string> UploadImageAsync(string fieldId, IFormFile file, string uploadedBy)
    {
        var safe = Path.GetFileName(file.FileName);
        var name = $"{fieldId}/{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}-{safe}";
        var blob = _container.GetBlobClient(name);
        await using var s = file.OpenReadStream();
        await blob.UploadAsync(s, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType },
            Metadata = new Dictionary<string, string> { ["uploadedBy"] = uploadedBy, ["fieldId"] = fieldId }
        });
        return name;
    }

    public async Task<List<MediaItem>> ListImagesAsync(int max = 50)
    {
        var list = new List<MediaItem>();
        await foreach (var b in _container.GetBlobsAsync())
        {
            list.Add(new MediaItem
            {
                Name = b.Name,
                Size = b.Properties.ContentLength ?? 0,
                UploadedAt = b.Properties.CreatedOn,
                Field = b.Name.Contains('/') ? b.Name[..b.Name.IndexOf('/')] : ""
            });
        }
        return list.OrderByDescending(i => i.UploadedAt).Take(max).ToList();
    }

    public async Task<(Stream Content, string ContentType)?> OpenImageAsync(string name)
    {
        var blob = _container.GetBlobClient(name);
        try
        {
            var r = await blob.DownloadStreamingAsync();
            return (r.Value.Content, r.Value.Details.ContentType);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    /// <summary>Latest processing-status audit rows written by the blob-triggered function.</summary>
    public async Task<List<TableEntity>> LatestAuditAsync(int max = 20)
    {
        var rows = new List<TableEntity>();
        await foreach (var e in _audit.QueryAsync<TableEntity>(maxPerPage: 200))
        {
            rows.Add(e);
            if (rows.Count >= 500) break;
        }
        return rows.OrderByDescending(r => r.GetDateTimeOffset("ReceivedAtUtc")).Take(max).ToList();
    }

    /// <summary>Direct Table Storage read (same shape as the GetLatestTelemetry function); used when Functions is down.</summary>
    public async Task<List<object>> LatestReadingsAsync(string fieldId, int count = 20)
    {
        var rows = new List<object>();
        await foreach (var e in _telemetry.QueryAsync<TableEntity>(x => x.PartitionKey == fieldId))
        {
            rows.Add(new
            {
                readingAtUtc = e.GetDateTimeOffset("ReadingAtUtc"),
                deviceId = e.GetString("DeviceId"),
                soilMoisture = e.GetDouble("SoilMoisture"),
                soilTemperature = e.GetDouble("SoilTemperature"),
                soilPh = e.GetDouble("SoilPh"),
                airTemperature = e.GetDouble("AirTemperature"),
                humidity = e.GetDouble("Humidity")
            });
            if (rows.Count >= count) break; // RowKey is inverted ticks, so the first rows are the newest
        }
        return rows;
    }

    /// <summary>Writes one sensor reading. RowKey is inverted ticks so newest rows sort first.</summary>
    public Task AddReadingAsync(string fieldId, string deviceId, double moisture, double soilTemp, double ph, double airTemp, double humidity, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var e = new TableEntity(fieldId, $"{DateTime.MaxValue.Ticks - now.Ticks:d19}-{deviceId}")
        {
            ["DeviceId"] = deviceId,
            ["SoilMoisture"] = moisture,
            ["SoilTemperature"] = soilTemp,
            ["SoilPh"] = ph,
            ["AirTemperature"] = airTemp,
            ["Humidity"] = humidity,
            ["ReadingAtUtc"] = now
        };
        return _telemetry.AddEntityAsync(e);
    }
}
