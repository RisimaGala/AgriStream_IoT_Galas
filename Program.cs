using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddApplicationInsightsTelemetry(); // uses APPLICATIONINSIGHTS_CONNECTION_STRING when present

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddSignalR();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<AgronomyService>();
builder.Services.AddHttpClient<FunctionsClient>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();

// Behind App Service front ends the original scheme arrives in X-Forwarded-Proto.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DbSeeder.Seed(scope.ServiceProvider.GetRequiredService<AppDbContext>(), app.Configuration);
    try
    {
        await scope.ServiceProvider.GetRequiredService<StorageService>().EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        // Don't crash the site if storage is unreachable (e.g. Azurite not running locally).
        app.Logger.LogWarning(ex, "Storage initialisation failed - is Azurite / the storage account reachable? Blob/Table features will fail until it is.");
    }
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow })); // used by load tests
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
