# Scale-out rules for the App Service plan: +1 instance when avg CPU > 60% (5 min),
# -1 instance when avg CPU < 30% (10 min). Memory rule added as a second scale-out trigger.
param(
    [Parameter(Mandatory)][string]$Suffix,
    [int]$Min = 1, [int]$Max = 5, [int]$Default = 1
)
$ErrorActionPreference = "Stop"
$rg = "rg-agristream-$Suffix"; $plan = "plan-agristream-$Suffix"
$planId = az appservice plan show -g $rg -n $plan --query id -o tsv

az monitor autoscale create -g $rg --resource $planId --resource-type Microsoft.Web/serverfarms `
    -n "autoscale-$plan" --min-count $Min --max-count $Max --count $Default | Out-Null

az monitor autoscale rule create -g $rg --autoscale-name "autoscale-$plan" `
    --condition "CpuPercentage > 60 avg 5m" --scale out 1 --cooldown 5 | Out-Null
az monitor autoscale rule create -g $rg --autoscale-name "autoscale-$plan" `
    --condition "MemoryPercentage > 75 avg 5m" --scale out 1 --cooldown 5 | Out-Null
az monitor autoscale rule create -g $rg --autoscale-name "autoscale-$plan" `
    --condition "CpuPercentage < 30 avg 10m" --scale in 1 --cooldown 10 | Out-Null

az monitor autoscale show -g $rg -n "autoscale-$plan" -o table
Write-Host "Autoscale configured: $Min-$Max instances. Portal: App Service plan > Scale out (App Service plan)."
