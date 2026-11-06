# Sends a fake sensor reading for every valve every few seconds so the dashboard has live data.
# Usage:  .\Simulate-Sensors.ps1            (Ctrl+C to stop)
param(
    [string]$BaseUrl = "https://localhost:7155",
    [string]$ApiKey  = "change-me-sensor-key",   # must match Ingest:ApiKey in appsettings.json
    [int]$IntervalSeconds = 5
)

$devices = @(
    @{ Field = "field-north"; Device = "valve-01" }, @{ Field = "field-north"; Device = "valve-02" },
    @{ Field = "field-north"; Device = "valve-03" }, @{ Field = "field-south"; Device = "valve-04" },
    @{ Field = "field-south"; Device = "valve-05" }, @{ Field = "field-south"; Device = "valve-06" }
)
$rnd = New-Object System.Random
function R($min, $max) { [math]::Round($min + ($max - $min) * $rnd.NextDouble(), 1) }

while ($true) {
    foreach ($d in $devices) {
        $body = @{
            fieldId = $d.Field; deviceId = $d.Device
            soilMoisture = R 20 60; soilTemperature = R 14 28; soilPh = R 5.8 7.4
            airTemperature = R 15 34; humidity = R 30 80
        } | ConvertTo-Json
        try {
            Invoke-RestMethod -Uri "$BaseUrl/api/ingest" -Method Post -Body $body `
                -ContentType "application/json" -Headers @{ "X-Api-Key" = $ApiKey } | Out-Null
            Write-Host "$(Get-Date -f HH:mm:ss) sent $($d.Device)"
        } catch { Write-Warning "$($d.Device): $($_.Exception.Message)" }
    }
    Start-Sleep -Seconds $IntervalSeconds
}
