using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AgriStream.Functions;

public record IrrigationCommand(
    string CommandId, string DeviceId, string FieldId, string Action,
    int DurationMinutes, string RequestedBy, string Source, DateTime RequestedAtUtc);

public record IrrigationInput(string? DeviceId, string? FieldId, string? Action, int? DurationMinutes, string? RequestedBy, string? Source);

/// <summary>
/// The four functions of the AgriStream pipeline:
///   1. ProcessDroneImage         - Blob trigger  -> audit row in Table Storage
///   2. GetLatestTelemetry        - HTTP GET      -> reads Table Storage
///   3. TriggerIrrigation         - HTTP POST     -> enqueues IrrigationCommand
///   4. ProcessIrrigationCommand  - Queue trigger -> updates device registry in Azure SQL
/// </summary>
public class Functions
{
    public const string TelemetryTable = "SensorReadings";
    public const string AuditTable = "ImageProcessingAudit";
    public const string EventsTable = "IrrigationEvents";
    public const string CommandQueue = "irrigation-commands";

    private static readonly string StorageCs = Environment.GetEnvironmentVariable("StorageConnection") ?? "UseDevelopmentStorage=true";
    private static readonly TableServiceClient Tables = new(StorageCs);
    private static readonly QueueClient Queue = new(StorageCs, CommandQueue);

    private readonly ILogger<Functions> _log;
    public Functions(ILogger<Functions> log) => _log = log;

    // 1 ------------------------------------------------------------------------------------------
    [Function("ProcessDroneImage")]
    public async Task ProcessDroneImage(
        [BlobTrigger("drone-images/{name}", Connection = "StorageConnection")] Stream blob,
        string name)
    {
        _log.LogInformation("Drone image uploaded: {Name} ({Length} bytes)", name, blob.Length);
        var audit = Tables.GetTableClient(AuditTable);
        await audit.CreateIfNotExistsAsync();

        var field = name.Contains('/') ? name[..name.IndexOf('/')] : "unknown";
        var entity = new TableEntity(field, $"{DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks:d19}-{Guid.NewGuid():N}")
        {
            ["BlobName"] = name,
            ["SizeBytes"] = blob.Length,
            ["Status"] = "Received",
            ["ReceivedAtUtc"] = DateTime.UtcNow,
            ["Processor"] = nameof(ProcessDroneImage)
        };
        await audit.AddEntityAsync(entity);
    }

    // 2 ------------------------------------------------------------------------------------------
    [Function("GetLatestTelemetry")]
    public async Task<IActionResult> GetLatestTelemetry(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "telemetry/{fieldId}")] HttpRequest req,
        string fieldId)
    {
        var count = int.TryParse(req.Query["count"], out var c) ? Math.Clamp(c, 1, 200) : 20;
        var table = Tables.GetTableClient(TelemetryTable);
        await table.CreateIfNotExistsAsync();

        // Single-partition query; RowKey is inverted ticks so the first rows returned are the newest.
        var rows = new List<object>();
        var safeField = fieldId.Replace("'", "''");
        await foreach (var e in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{safeField}'", maxPerPage: count))
        {
            rows.Add(new
            {
                fieldId = e.PartitionKey,
                deviceId = e.GetString("DeviceId"),
                soilMoisture = e.GetDouble("SoilMoisture"),
                soilTemperature = e.GetDouble("SoilTemperature"),
                soilPh = e.GetDouble("SoilPh"),
                airTemperature = e.GetDouble("AirTemperature"),
                humidity = e.GetDouble("Humidity"),
                readingAtUtc = e.GetDateTime("ReadingAtUtc")?.ToString("u")
            });
            if (rows.Count >= count) break;
        }
        return new OkObjectResult(rows);
    }

    // 3 ------------------------------------------------------------------------------------------
    [Function("TriggerIrrigation")]
    public async Task<IActionResult> TriggerIrrigation(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "irrigation")] HttpRequest req)
    {
        IrrigationInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<IrrigationInput>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return new BadRequestObjectResult(new { error = "Invalid JSON" }); }

        if (input is null || string.IsNullOrWhiteSpace(input.DeviceId) || string.IsNullOrWhiteSpace(input.FieldId))
            return new BadRequestObjectResult(new { error = "deviceId and fieldId are required" });

        var action = string.Equals(input.Action, "STOP", StringComparison.OrdinalIgnoreCase) ? "STOP" : "START";
        var cmd = new IrrigationCommand(Guid.NewGuid().ToString("N"), input.DeviceId!, input.FieldId!, action,
            Math.Clamp(input.DurationMinutes ?? 15, 1, 120), input.RequestedBy ?? "unknown", input.Source ?? "Sensor", DateTime.UtcNow);

        await Queue.CreateIfNotExistsAsync();
        // Base64 matches host.json "messageEncoding": "base64" so the queue trigger can decode it.
        await Queue.SendMessageAsync(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cmd)));

        _log.LogInformation("Queued irrigation command {Id} {Action} for {Device}", cmd.CommandId, cmd.Action, cmd.DeviceId);
        return new AcceptedResult(string.Empty, new { commandId = cmd.CommandId, status = "Queued" });
    }

    // 4 ------------------------------------------------------------------------------------------
    [Function("ProcessIrrigationCommand")]
    public async Task ProcessIrrigationCommand(
        [QueueTrigger(CommandQueue, Connection = "StorageConnection")] string message)
    {
        var cmd = JsonSerializer.Deserialize<IrrigationCommand>(message,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Empty irrigation command");

        var status = cmd.Action == "START" ? "Irrigating" : "Idle";

        // Update the master device registry in Azure SQL.
        await using (var conn = new SqlConnection(Environment.GetEnvironmentVariable("SqlConnection")))
        {
            await conn.OpenAsync();
            await using var sql = new SqlCommand(
                "UPDATE Devices SET Status=@s, LastCommand=@c, LastCommandAtUtc=@t WHERE DeviceId=@id", conn);
            sql.Parameters.AddWithValue("@s", status);
            sql.Parameters.AddWithValue("@c", cmd.Action);
            sql.Parameters.AddWithValue("@t", DateTime.UtcNow);
            sql.Parameters.AddWithValue("@id", cmd.DeviceId);
            var n = await sql.ExecuteNonQueryAsync();
            if (n == 0) _log.LogWarning("Device {Device} not found in registry", cmd.DeviceId);
        }

        // Keep an event log in Table Storage (irrigation events are part of the telemetry store).
        var events = Tables.GetTableClient(EventsTable);
        await events.CreateIfNotExistsAsync();
        await events.UpsertEntityAsync(new TableEntity(cmd.FieldId, $"{DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks:d19}-{cmd.CommandId}")
        {
            ["DeviceId"] = cmd.DeviceId,
            ["Action"] = cmd.Action,
            ["DurationMinutes"] = cmd.DurationMinutes,
            ["RequestedBy"] = cmd.RequestedBy,
            ["Source"] = cmd.Source,
            ["ProcessedAtUtc"] = DateTime.UtcNow
        });

        await NotifyWebAppAsync(cmd, status);

        _log.LogInformation("Processed irrigation command {Id}: {Device} -> {Status}", cmd.CommandId, cmd.DeviceId, status);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Best-effort push so open dashboards update instantly; failures never fail the command.</summary>
    private async Task NotifyWebAppAsync(IrrigationCommand cmd, string status)
    {
        try
        {
            var baseUrl = (Environment.GetEnvironmentVariable("WebAppUrl") ?? "https://localhost:7155").TrimEnd('/');
            var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/device-status")
            {
                Content = JsonContent.Create(new
                {
                    fieldId = cmd.FieldId, deviceId = cmd.DeviceId, status,
                    lastCommand = cmd.Action, lastCommandAtUtc = DateTime.UtcNow
                })
            };
            req.Headers.Add("X-Api-Key", Environment.GetEnvironmentVariable("IngestApiKey") ?? "change-me-sensor-key");
            (await Http.SendAsync(req)).EnsureSuccessStatusCode();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not notify web app of status change for {Device}", cmd.DeviceId); }
    }
}
