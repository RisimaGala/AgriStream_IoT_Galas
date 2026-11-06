# AgriStream IoT

ASP.NET Core 8 MVC management app plus an Azure Functions pipeline for soil telemetry, drone imagery and irrigation control.

```
Browser ─► App Service (MVC) ──► Azure SQL (users, roles, device registry)
              │  │
              │  └─ upload ───► Blob "drone-images" ──(blob trigger)──► F1 ProcessDroneImage ─► Table ImageProcessingAudit
              │
              ├─ GET  ─► F2 GetLatestTelemetry ───────► Table SensorReadings
              └─ POST ─► F3 TriggerIrrigation ─► Queue "irrigation-commands" ─(queue trigger)─► F4 ProcessIrrigationCommand
                                                                                    ├─► Azure SQL Devices (status)
                                                                                    └─► Table IrrigationEvents
```

Exactly four functions (`AgriStream.Functions/Functions.cs`): the blob trigger, HTTP GET, HTTP POST, and the queue trigger that processes the command the POST enqueues and updates the SQL device registry.

## Layout
| Path | Purpose |
|---|---|
| `Controllers/ Views/ Models/ Data/ Services/` | MVC app (cookie auth, roles Farmer / Agronomist / Admin, PBKDF2 password hashes) |
| `AgriStream.Functions/` | The four Azure Functions (.NET 8 isolated) |
| `infra/deploy.ps1` | Creates all Azure resources and deploys both apps |
| `infra/autoscale.ps1` | Scale-out rules (CPU > 60 %, memory > 75 %, scale in CPU < 30 %) |
| `loadtest/LoadTest.ps1` | Concurrent-user load script |

## Roles
- **Farmer** – self-registers; sees only devices they own; can trigger irrigation, upload imagery. Farmer--> USERNAME: Ronny	 PASSWORD: RonnyCR7


- **Agronomist** – sees all fields. Agronomist--> USERNAME:	Orlando Barker	  PASSWORD: Orlando2324


- **Admin** – everything, plus user/role management and a simulated-telemetry button. Admin--> USERNAME: Sima Gala  	PASSWORD: SimaGala03

Seed accounts (local): `admin / Admin#12345`, `farmer1 / Farmer#12345`, `agro1 / Agro#12345`. In Azure the admin password is the `-SqlAdminPassword` you pass to `deploy.ps1`. **Change these after first login.**

## Run locally
1. Start Azurite (`azurite`) and have SQL LocalDB (or change the connection string).
2. `cd AgriStream.Functions && func start` (needs Azure Functions Core Tools v4).
3. `dotnet run` in the repo root, open the URL, sign in as `admin`, press **Generate simulated sensor readings**, then open the Dashboard as `farmer1`.

Sensors can also POST readings to `/api/ingest` with header `X-Api-Key` (`Ingest:ApiKey`).

## Deploy to Azure
```powershell
az login
.\infra\deploy.ps1 -Suffix galas01 -SqlAdminPassword '<strong password>'
.\infra\autoscale.ps1 -Suffix galas01
```
The plan is Standard **S1** (autoscale needs Standard or higher). Resources: App Service + plan, Function App, Storage (blob/table/queue), Azure SQL S0, Log Analytics + Application Insights.

## Load test → monitor → autoscale evidence
1. Baseline: open **Plan → Scale out** and note instance count = 1.
2. Run (PowerShell 7): `.\loadtest\LoadTest.ps1 -BaseUrl https://agristream-web-galas01.azurewebsites.net -Users 150 -DurationSec 900`
   (Alternatively use Azure Load Testing / JMeter against the same URLs.)
3. Screenshots to take while it runs:
   - **App Service plan → Metrics**: CPU Percentage / Memory Percentage (Avg) – shows the threshold crossing.
   - **Plan → Scale out → Run history** (and **Monitor → Autoscale → Run history**): instance count rising 1 → N.
   - **Application Insights → Performance / Live Metrics / Failures**: response time, request rate, failures.
   - **Function App → Monitor** or App Insights: function invocations (queue trigger keeping up).
   - Final output line of the load script (requests, failure %, avg/max latency).
4. Useful Application Insights (Logs) queries:
```kusto
requests | where timestamp > ago(30m) | summarize count(), avg(duration), percentile(duration,95), failed=countif(success==false) by bin(timestamp,1m) | render timechart
requests | where cloud_RoleName has "func" | summarize count() by name, bin(timestamp,1m)
```
5. Judge against targets such as p95 < 1 s, failure rate < 1 %, and no unprocessed backlog in the `irrigation-commands` queue (check Storage → Queues; message count returns to 0).

## Notes / limits
- Sensor ingest writes directly to Table Storage from the MVC app so the pipeline stays at exactly four functions.
- Schema is created with `EnsureCreated` for simplicity; switch to EF migrations for production.
- Function auth uses a function key stored in App Service settings; keep keys out of source control.
