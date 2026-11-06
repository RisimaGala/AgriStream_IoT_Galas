using System.Text;
using System.Text.Json;

namespace AgriStream_IoT_Galas.Services;

public record IrrigationRequest(string DeviceId, string FieldId, string Action, int DurationMinutes, string RequestedBy, string Source);

/// <summary>Typed HttpClient for the two HTTP-triggered Azure Functions (GET telemetry / POST irrigation).</summary>
public class FunctionsClient
{
    private readonly HttpClient _http;
    public FunctionsClient(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _http.BaseAddress = new Uri((cfg["Functions:BaseUrl"] ?? "http://localhost:7071").TrimEnd('/') + "/");
        var key = cfg["Functions:Key"];
        if (!string.IsNullOrEmpty(key)) _http.DefaultRequestHeaders.Add("x-functions-key", key);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>Returns raw JSON array of latest readings (newest first).</summary>
    public async Task<string> GetLatestTelemetryAsync(string fieldId, int count = 20)
    {
        var r = await _http.GetAsync($"api/telemetry/{Uri.EscapeDataString(fieldId)}?count={count}");
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync();
    }

    public async Task<(bool ok, string body)> TriggerIrrigationAsync(IrrigationRequest req)
    {
        var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        var r = await _http.PostAsync("api/irrigation", content);
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }
}
