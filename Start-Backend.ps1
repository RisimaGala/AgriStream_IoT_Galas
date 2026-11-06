# Starts Azurite (storage emulator) and the Azure Functions host in their own windows.
# Run the web app from Visual Studio (F5) afterwards.  Usage:  .\Start-Backend.ps1
$root = $PSScriptRoot
$vsAzurite = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\Extensions\Microsoft\Azure Storage Emulator\azurite.exe"
$func = Get-ChildItem "$env:LOCALAPPDATA\AzureFunctionsTools\Releases\4.*\cli_x64\func.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1

if (-not (Get-NetTCPConnection -LocalPort 10002 -State Listen -ErrorAction SilentlyContinue)) {
    $data = Join-Path $env:USERPROFILE "azurite-data"; New-Item -ItemType Directory -Force $data | Out-Null
    Start-Process powershell -ArgumentList "-NoExit","-Command","& '$vsAzurite' --location '$data' --skipApiVersionCheck"
    Start-Sleep -Seconds 4
} else { Write-Host "Azurite already running." }

if (-not $func) { Write-Error "func.exe not found - open the Functions project once in Visual Studio to install Core Tools."; exit 1 }
Start-Process powershell -WorkingDirectory "$root\AgriStream.Functions" -ArgumentList "-NoExit","-Command","& '$($func.FullName)' start"
Write-Host "Started. Wait for 'Host started' in the Functions window, then run the web app."
