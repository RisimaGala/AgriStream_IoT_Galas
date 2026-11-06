# Simple concurrent-user load generator (no extra tooling needed).
# Each virtual user: login once, then loops over dashboard, telemetry (-> GET function), status,
# and occasionally POSTs an irrigation command (-> POST function -> queue -> SQL).
# Usage: .\loadtest\LoadTest.ps1 -BaseUrl https://agristream-web-<suffix>.azurewebsites.net -Users 100 -DurationSec 600
# Requires PowerShell 7 (ForEach-Object -Parallel).
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [int]$Users = 50,
    [int]$DurationSec = 300,
    [string]$Username = "farmer1",
    [string]$Password = "Farmer#12345",
    [string]$Field = "field-north"
)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw "Run with PowerShell 7+ (pwsh)." }
$BaseUrl = $BaseUrl.TrimEnd('/')
$end = (Get-Date).AddSeconds($DurationSec)

$results = 1..$Users | ForEach-Object -ThrottleLimit $Users -Parallel {
    $base = $using:BaseUrl; $end = $using:end
    $sess = $null; $stats = [ordered]@{ ok = 0; fail = 0; totalMs = 0.0; maxMs = 0.0 }
    function Hit($method, $url, $body = $null) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $p = @{ Uri = $url; Method = $method; WebSession = $script:sess; SkipHttpErrorCheck = $true; MaximumRedirection = 0; ErrorAction = 'Stop' }
            if ($body) { $p.Body = $body }
            $r = Invoke-WebRequest @p
            $good = [int]$r.StatusCode -lt 400 -or [int]$r.StatusCode -eq 302
        } catch { $good = $false }
        $sw.Stop()
        if ($good) { $stats.ok++ } else { $stats.fail++ }
        $stats.totalMs += $sw.Elapsed.TotalMilliseconds
        if ($sw.Elapsed.TotalMilliseconds -gt $stats.maxMs) { $stats.maxMs = $sw.Elapsed.TotalMilliseconds }
        return $r
    }
    # login (anti-forgery token + cookie)
    $script:sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $page = Hit GET "$base/Account/Login"
    $tok = [regex]::Match($page.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $null = Hit POST "$base/Account/Login" @{ Username = $using:Username; Password = $using:Password; __RequestVerificationToken = $tok }
    $i = 0
    while ((Get-Date) -lt $end) {
        $i++
        $null = Hit GET "$base/Dashboard?field=$($using:Field)"
        $null = Hit GET "$base/Dashboard/Telemetry?field=$($using:Field)"
        $null = Hit GET "$base/Dashboard/DeviceStatus?field=$($using:Field)"
        $null = Hit GET "$base/health"
        if ($i % 10 -eq 0) {
            $dash = Hit GET "$base/Dashboard?field=$($using:Field)"
            $t = [regex]::Match($dash.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
            $null = Hit POST "$base/Dashboard/TriggerIrrigation" @{ deviceId = 'valve-01'; action = 'START'; minutes = 5; __RequestVerificationToken = $t }
        }
        Start-Sleep -Milliseconds (Get-Random -Min 200 -Max 800)   # think time
    }
    [pscustomobject]$stats
}

$ok = ($results | Measure-Object ok -Sum).Sum; $fail = ($results | Measure-Object fail -Sum).Sum
$avg = ($results | Measure-Object totalMs -Sum).Sum / [math]::Max(1, $ok + $fail)
"{0} users, {1}s: {2} requests, {3} failed ({4:P2}), avg {5:N0} ms, max {6:N0} ms, ~{7:N1} req/s" -f `
    $Users, $DurationSec, ($ok + $fail), $fail, ($fail / [math]::Max(1, $ok + $fail)), $avg, ($results | Measure-Object maxMs -Maximum).Maximum, (($ok + $fail) / $DurationSec)
