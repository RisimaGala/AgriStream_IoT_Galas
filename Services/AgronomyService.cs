using AgriStream_IoT_Galas.Models;
using Azure.Data.Tables;

namespace AgriStream_IoT_Galas.Services;

/// <summary>Agronomist tooling: sensor analysis + rule-based advice, harvest history and scouting notes (Table Storage).</summary>
public class AgronomyService
{
    private const string TelemetryTable = "SensorReadings";
    private const string HarvestTable = "HarvestRecords";
    private const string NotesTable = "ScoutingNotes";

    private readonly TableClient _telemetry, _harvest, _notes;
    private volatile bool _ensured;

    public AgronomyService(IConfiguration cfg)
    {
        var tables = new TableServiceClient(cfg["Storage:ConnectionString"] ?? "UseDevelopmentStorage=true");
        _telemetry = tables.GetTableClient(TelemetryTable);
        _harvest = tables.GetTableClient(HarvestTable);
        _notes = tables.GetTableClient(NotesTable);
    }

    private async Task EnsureAsync()
    {
        if (_ensured) return;
        await _telemetry.CreateIfNotExistsAsync();
        await _harvest.CreateIfNotExistsAsync();
        await _notes.CreateIfNotExistsAsync();
        _ensured = true;
    }

    // ---- sensor analysis --------------------------------------------------------------------

    public async Task<FieldAnalysis> AnalyseAsync(string field, int samples = 60)
    {
        await EnsureAsync();
        var rows = new List<TableEntity>();
        // RowKey is inverted ticks, so the first rows returned are the newest.
        await foreach (var e in _telemetry.QueryAsync<TableEntity>(x => x.PartitionKey == field))
        {
            rows.Add(e);
            if (rows.Count >= samples) break;
        }
        rows.Reverse(); // oldest -> newest for trend maths and charts

        var a = new FieldAnalysis { Field = field, Samples = rows.Count };
        if (rows.Count == 0) return a;

        double Avg(string col) => Math.Round(rows.Average(r => r.GetDouble(col) ?? 0), 1);
        a.Moisture = Avg("SoilMoisture"); a.SoilTemp = Avg("SoilTemperature"); a.Ph = Math.Round(rows.Average(r => r.GetDouble("SoilPh") ?? 0), 2);
        a.AirTemp = Avg("AirTemperature"); a.Humidity = Avg("Humidity");
        a.MoistureSeries = rows.Select(r => Math.Round(r.GetDouble("SoilMoisture") ?? 0, 1)).ToList();
        a.SoilTempSeries = rows.Select(r => Math.Round(r.GetDouble("SoilTemperature") ?? 0, 1)).ToList();
        a.Labels = rows.Select(r => r.GetDateTimeOffset("ReadingAtUtc")?.ToString("HH:mm:ss") ?? "").ToList();

        if (rows.Count >= 6)
        {
            var half = rows.Count / 2;
            a.MoistureTrend = Math.Round(a.MoistureSeries.Skip(half).Average() - a.MoistureSeries.Take(half).Average(), 1);
        }
        a.Recommendations = Advise(a);
        return a;
    }

    /// <summary>Simple agronomic rules of thumb. They are guidance for the agronomist, not a replacement for lab soil tests.</summary>
    private static List<Recommendation> Advise(FieldAnalysis a)
    {
        var r = new List<Recommendation>();

        if (a.Moisture < 30) r.Add(new("danger", "Irrigation", $"Soil moisture is low ({a.Moisture}%). Schedule irrigation now, preferably early morning to limit evaporation."));
        else if (a.Moisture < 38) r.Add(new("warning", "Irrigation", $"Soil moisture ({a.Moisture}%) is approaching the dry range. Plan the next irrigation cycle."));
        else if (a.Moisture > 60) r.Add(new("warning", "Irrigation", $"Soil is very wet ({a.Moisture}%). Hold irrigation and check drainage to avoid root rot and waterlogging."));
        else r.Add(new("ok", "Irrigation", $"Soil moisture ({a.Moisture}%) is in the comfortable range. No irrigation needed."));

        if (a.MoistureTrend <= -5) r.Add(new("warning", "Irrigation", $"Moisture is falling quickly ({a.MoistureTrend} points over the sampled period). Expect to irrigate soon."));
        else if (a.MoistureTrend >= 5) r.Add(new("ok", "Irrigation", $"Moisture is rising ({a.MoistureTrend:+0.0} points), consistent with recent irrigation or rain."));

        if (a.Ph < 5.8) r.Add(new("danger", "Soil", $"Soil is acidic (pH {a.Ph}). Consider liming and a soil lab test before the next planting."));
        else if (a.Ph < 6.0) r.Add(new("warning", "Soil", $"pH {a.Ph} is slightly acidic; watch nutrient availability (phosphorus, molybdenum)."));
        else if (a.Ph > 7.5) r.Add(new("danger", "Soil", $"Soil is alkaline (pH {a.Ph}). Consider elemental sulphur or acidifying fertiliser; iron and zinc may be locked up."));
        else if (a.Ph > 7.2) r.Add(new("warning", "Soil", $"pH {a.Ph} is slightly alkaline. Monitor micronutrient deficiency symptoms."));
        else r.Add(new("ok", "Fertiliser", $"pH {a.Ph} is in the ideal 6.0 to 7.2 window, so nutrients are readily available. Fertilise according to the crop plan."));

        if (a.SoilTemp < 10) r.Add(new("warning", "Climate", $"Soil temperature is cool ({a.SoilTemp} °C). Germination of warm-season crops will be slow; delay planting."));
        else if (a.SoilTemp > 30) r.Add(new("warning", "Climate", $"Soil temperature is high ({a.SoilTemp} °C). Root stress is likely; consider mulching and more frequent, shorter irrigation."));

        if (a.AirTemp > 32) r.Add(new("warning", "Climate", $"Air temperature is high ({a.AirTemp} °C). Avoid applying chemicals or fertiliser in the heat of the day."));

        if (a.Humidity > 75 && a.AirTemp is > 15 and < 30)
            r.Add(new("warning", "Disease", $"High humidity ({a.Humidity}%) with mild temperatures favours fungal disease. Scout the field and record any findings."));

        return r;
    }

    // ---- harvest history --------------------------------------------------------------------

    public async Task<List<HarvestRecord>> HarvestsAsync(string? field = null)
    {
        await EnsureAsync();
        var list = new List<HarvestRecord>();
        var q = field == null ? _harvest.QueryAsync<TableEntity>() : _harvest.QueryAsync<TableEntity>(x => x.PartitionKey == field);
        await foreach (var e in q)
            list.Add(new HarvestRecord
            {
                Field = e.PartitionKey, Season = e.GetString("Season") ?? "", Crop = e.GetString("Crop") ?? "",
                AreaHa = e.GetDouble("AreaHa") ?? 0, YieldTonnes = e.GetDouble("YieldTonnes") ?? 0, RecordedBy = e.GetString("RecordedBy") ?? ""
            });
        return list.OrderBy(h => h.Season).ThenBy(h => h.Crop).ToList();
    }

    public async Task AddHarvestAsync(HarvestRecord h)
    {
        await EnsureAsync();
        await _harvest.UpsertEntityAsync(new TableEntity(h.Field, $"{h.Season}-{h.Crop}".Replace('/', '-'))
        {
            ["Season"] = h.Season, ["Crop"] = h.Crop, ["AreaHa"] = h.AreaHa, ["YieldTonnes"] = h.YieldTonnes, ["RecordedBy"] = h.RecordedBy
        });
    }

    /// <summary>Compares the two latest seasons of the SAME crop (maize vs soybean yields are not comparable).</summary>
    public static string? YieldInsight(List<HarvestRecord> h)
    {
        if (h.Count == 0) return null;
        foreach (var crop in h.OrderByDescending(x => x.Season).Select(x => x.Crop).Distinct())
        {
            var rows = h.Where(x => x.Crop == crop).OrderBy(x => x.Season).ToList();
            if (rows.Count < 2) continue;
            var prev = rows[^2]; var last = rows[^1];
            if (prev.YieldPerHa <= 0) continue;
            var pct = Math.Round((last.YieldPerHa - prev.YieldPerHa) / prev.YieldPerHa * 100, 1);
            return $"{crop}: yield per hectare {(pct >= 0 ? "rose" : "fell")} {Math.Abs(pct)}% from {prev.Season} ({prev.YieldPerHa:0.0} t/ha) to {last.Season} ({last.YieldPerHa:0.0} t/ha).";
        }
        return "Add two seasons of the same crop to see a yield trend.";
    }

    /// <summary>Inserts clearly labelled sample history so the yield charts can be demonstrated.</summary>
    public async Task SeedSampleHarvestsAsync(IEnumerable<string> fields, string by)
    {
        var rnd = new Random(42);
        foreach (var f in fields)
            foreach (var (season, crop) in new[] { ("2022/23", "Maize"), ("2023/24", "Soybean"), ("2024/25", "Maize"), ("2025/26", "Soybean") })
            {
                var area = 40.0;
                var perHa = crop == "Maize" ? 6.5 + rnd.NextDouble() * 2.5 : 2.4 + rnd.NextDouble() * 1.0;
                await AddHarvestAsync(new HarvestRecord { Field = f, Season = season, Crop = crop, AreaHa = area, YieldTonnes = Math.Round(perHa * area, 1), RecordedBy = by + " (sample data)" });
            }
    }

    // ---- scouting notes ---------------------------------------------------------------------

    public async Task<List<ScoutNote>> NotesAsync(string field, int max = 15)
    {
        await EnsureAsync();
        var list = new List<ScoutNote>();
        await foreach (var e in _notes.QueryAsync<TableEntity>(x => x.PartitionKey == field))
        {
            list.Add(new ScoutNote
            {
                Field = field, RecordedAtUtc = e.GetDateTimeOffset("RecordedAtUtc"), Category = e.GetString("Category") ?? "",
                Severity = e.GetString("Severity") ?? "", Note = e.GetString("Note") ?? "", Advice = e.GetString("Advice") ?? "",
                RecordedBy = e.GetString("RecordedBy") ?? ""
            });
            if (list.Count >= max) break; // newest first (inverted ticks)
        }
        return list;
    }

    public async Task AddNoteAsync(ScoutNote n)
    {
        await EnsureAsync();
        var now = DateTime.UtcNow;
        await _notes.AddEntityAsync(new TableEntity(n.Field, $"{DateTime.MaxValue.Ticks - now.Ticks:d19}-{Guid.NewGuid():N}"[..40])
        {
            ["RecordedAtUtc"] = now, ["Category"] = n.Category, ["Severity"] = n.Severity,
            ["Note"] = n.Note, ["Advice"] = n.Advice, ["RecordedBy"] = n.RecordedBy
        });
    }
}
