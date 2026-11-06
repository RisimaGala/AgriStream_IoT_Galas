# Provisions AgriStream IoT on Azure and deploys both apps.
# Prereqs: az login, Azure Functions Core Tools not required (uses zip deploy), .NET 8 SDK.
# Usage:  .\infra\deploy.ps1 -Suffix galas01 -SqlAdminPassword '<strong password>'
param(
    [Parameter(Mandatory)][string]$Suffix,            # globally-unique suffix, lowercase letters/digits
    [Parameter(Mandatory)][string]$SqlAdminPassword,
    [string]$Location = "southafricanorth",
    [string]$SqlAdminUser = "agriadmin"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$rg = "rg-agristream-$Suffix"; $plan = "plan-agristream-$Suffix"; $web = "agristream-web-$Suffix"
$func = "agristream-func-$Suffix"; $sa = "stagri$Suffix"; $sqlsrv = "sql-agristream-$Suffix"
$db = "AgriStream"; $ai = "appi-agristream-$Suffix"; $law = "law-agristream-$Suffix"

az group create -n $rg -l $Location | Out-Null

# --- Monitoring: Log Analytics + Application Insights -------------------------------------------------
az monitor log-analytics workspace create -g $rg -n $law -l $Location | Out-Null
$lawId = az monitor log-analytics workspace show -g $rg -n $law --query id -o tsv
az extension add --name application-insights --only-show-errors 2>$null
az monitor app-insights component create -g $rg -a $ai -l $Location --workspace $lawId --kind web | Out-Null
$aiCs = az monitor app-insights component show -g $rg -a $ai --query connectionString -o tsv

# --- Storage (Blob + Table + Queue) -------------------------------------------------------------------
az storage account create -g $rg -n $sa -l $Location --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false | Out-Null
$saCs = az storage account show-connection-string -g $rg -n $sa --query connectionString -o tsv
az storage container create --name drone-images --connection-string $saCs | Out-Null
foreach ($t in "SensorReadings", "ImageProcessingAudit", "IrrigationEvents") { az storage table create --name $t --connection-string $saCs | Out-Null }
az storage queue create --name irrigation-commands --connection-string $saCs | Out-Null

# --- Azure SQL ---------------------------------------------------------------------------------------
az sql server create -g $rg -n $sqlsrv -l $Location -u $SqlAdminUser -p $SqlAdminPassword | Out-Null
az sql server firewall-rule create -g $rg -s $sqlsrv -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 | Out-Null
az sql db create -g $rg -s $sqlsrv -n $db --service-objective S0 | Out-Null
$sqlCs = "Server=tcp:$sqlsrv.database.windows.net,1433;Initial Catalog=$db;User ID=$SqlAdminUser;Password=$SqlAdminPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# --- App Service plan (Standard S1 supports autoscale) + web app ----------------------------------------
az appservice plan create -g $rg -n $plan -l $Location --sku S1 | Out-Null
az webapp create -g $rg -p $plan -n $web --runtime "dotnet:8" | Out-Null

# --- Function app (Consumption would also work; Linux-free Windows plan keeps it simple) ---------------
az functionapp create -g $rg -n $func -s $sa --consumption-plan-location $Location `
    --runtime dotnet-isolated --runtime-version 8 --functions-version 4 --os-type Windows `
    --app-insights $ai | Out-Null
az functionapp config appsettings set -g $rg -n $func --settings `
    "StorageConnection=$saCs" "SqlConnection=$sqlCs" "APPLICATIONINSIGHTS_CONNECTION_STRING=$aiCs" | Out-Null

# --- Build + zip deploy Functions ----------------------------------------------------------------------
$pub = Join-Path $env:TEMP "agri-func-pub"; $zip = Join-Path $env:TEMP "agri-func.zip"
Remove-Item $pub, $zip -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\AgriStream.Functions\AgriStream.Functions.csproj" -c Release -o $pub
Compress-Archive "$pub\*" $zip
az functionapp deployment source config-zip -g $rg -n $func --src $zip | Out-Null

# Function key for the web app
$fkey = az functionapp keys list -g $rg -n $func --query functionKeys.default -o tsv
$funcUrl = "https://" + (az functionapp show -g $rg -n $func --query defaultHostName -o tsv)

# --- Configure + deploy web app -------------------------------------------------------------------------
az webapp config appsettings set -g $rg -n $web --settings `
    "ConnectionStrings__DefaultConnection=$sqlCs" "Storage__ConnectionString=$saCs" `
    "Functions__BaseUrl=$funcUrl" "Functions__Key=$fkey" "Ingest__ApiKey=$([guid]::NewGuid().ToString('N'))" `
    "Seed__AdminPassword=$SqlAdminPassword" "APPLICATIONINSIGHTS_CONNECTION_STRING=$aiCs" `
    "ApplicationInsightsAgent_EXTENSION_VERSION=~3" | Out-Null
$wpub = Join-Path $env:TEMP "agri-web-pub"; $wzip = Join-Path $env:TEMP "agri-web.zip"
Remove-Item $wpub, $wzip -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\AgriStream_IoT_Galas.csproj" -c Release -o $wpub
Compress-Archive "$wpub\*" $wzip
az webapp deploy -g $rg -n $web --src-path $wzip --type zip | Out-Null

Write-Host "`nWeb app : https://$web.azurewebsites.net"
Write-Host "Functions: $funcUrl"
Write-Host "Admin login: admin / <the SQL admin password you supplied>"
Write-Host "Next: .\infra\autoscale.ps1 -Suffix $Suffix"
