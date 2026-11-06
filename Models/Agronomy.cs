namespace AgriStream_IoT_Galas.Models;

/// <summary>One season's harvest for a field (long-term record the agronomist uses to compare yields).</summary>
public class HarvestRecord
{
    public string Field { get; set; } = "";
    public string Season { get; set; } = "";
    public string Crop { get; set; } = "";
    public double AreaHa { get; set; }
    public double YieldTonnes { get; set; }
    public double YieldPerHa => AreaHa > 0 ? Math.Round(YieldTonnes / AreaHa, 2) : 0;
    public string RecordedBy { get; set; } = "";
}

/// <summary>Field scouting observation: pest, disease or weed, with the agronomist's advice.</summary>
public class ScoutNote
{
    public string Field { get; set; } = "";
    public DateTimeOffset? RecordedAtUtc { get; set; }
    public string Category { get; set; } = "Pest";
    public string Severity { get; set; } = "Low";
    public string Note { get; set; } = "";
    public string Advice { get; set; } = "";
    public string RecordedBy { get; set; } = "";
}

public class FieldAnalysis
{
    public string Field { get; set; } = "";
    public int Samples { get; set; }
    public double Moisture { get; set; }
    public double SoilTemp { get; set; }
    public double Ph { get; set; }
    public double AirTemp { get; set; }
    public double Humidity { get; set; }
    public double MoistureTrend { get; set; }          // second half average minus first half average
    public List<Recommendation> Recommendations { get; set; } = new();
    public List<double> MoistureSeries { get; set; } = new();   // oldest -> newest
    public List<double> SoilTempSeries { get; set; } = new();
    public List<string> Labels { get; set; } = new();
}

/// <summary>Level: danger, warning, ok. Topic: Irrigation, Soil, Fertiliser, Disease, Climate.</summary>
public record Recommendation(string Level, string Topic, string Text);

public class AgronomistViewModel
{
    public List<string> Fields { get; set; } = new();
    public string? SelectedField { get; set; }
    public FieldAnalysis? Analysis { get; set; }
    public List<Device> Devices { get; set; } = new();
    public List<HarvestRecord> Harvests { get; set; } = new();
    public List<ScoutNote> Notes { get; set; } = new();
    public List<string> Crops { get; set; } = new();
    public string? YieldInsight { get; set; }
}
