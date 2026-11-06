using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace AgriStream_IoT_Galas.Controllers;

public record ReadingDto(string FieldId, string DeviceId, double SoilMoisture, double SoilTemperature, double SoilPh, double AirTemperature, double Humidity);

/// <summary>
/// Sensor gateway endpoint (API-key protected). It writes straight to Table Storage so the
/// four-function pipeline stays exactly as specified.
/// </summary>
[ApiController]
[Route("api/ingest")]
public class IngestController : ControllerBase
{
    private readonly StorageService _storage;
    private readonly IConfiguration _cfg;
    private readonly IHubContext<TelemetryHub> _hub;
    public IngestController(StorageService storage, IConfiguration cfg, IHubContext<TelemetryHub> hub)
    { _storage = storage; _cfg = cfg; _hub = hub; }

    public record DeviceStatusDto(string FieldId, string DeviceId, string Status, string? LastCommand, DateTime? LastCommandAtUtc);

    /// <summary>Called by the queue-triggered function after it updates the device registry; fans the change out to dashboards.</summary>
    [HttpPost("/api/device-status")]
    public async Task<IActionResult> DeviceStatus([FromBody] DeviceStatusDto s, [FromHeader(Name = "X-Api-Key")] string? key)
    {
        if (string.IsNullOrEmpty(key) || key != _cfg["Ingest:ApiKey"]) return Unauthorized();
        await _hub.Clients.Group(TelemetryHub.Group(s.FieldId)).SendAsync("deviceStatus", s);
        return Accepted();
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ReadingDto r, [FromHeader(Name = "X-Api-Key")] string? key)
    {
        if (string.IsNullOrEmpty(key) || key != _cfg["Ingest:ApiKey"]) return Unauthorized();
        await _storage.AddReadingAsync(r.FieldId, r.DeviceId, r.SoilMoisture, r.SoilTemperature, r.SoilPh, r.AirTemperature, r.Humidity);
        await _hub.Clients.Group(TelemetryHub.Group(r.FieldId)).SendAsync("reading", new
        {
            readingAtUtc = DateTime.UtcNow,
            deviceId = r.DeviceId,
            soilMoisture = r.SoilMoisture,
            soilTemperature = r.SoilTemperature,
            soilPh = r.SoilPh,
            airTemperature = r.AirTemperature,
            humidity = r.Humidity
        });
        return Accepted();
    }
}
