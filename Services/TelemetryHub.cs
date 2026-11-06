using System.Security.Claims;
using AgriStream_IoT_Galas.Data;
using AgriStream_IoT_Galas.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgriStream_IoT_Galas.Services;

/// <summary>Pushes new sensor readings to dashboards. Clients join one group per field they may see.</summary>
[Authorize]
public class TelemetryHub : Hub
{
    private readonly AppDbContext _db;
    public TelemetryHub(AppDbContext db) => _db = db;

    public static string Group(string field) => $"field:{field}";

    public async Task JoinField(string field)
    {
        var user = Context.User!;
        var allowed = user.IsInRole(Roles.Farmer)
            ? await _db.Devices.AnyAsync(d => d.FieldId == field &&
                  d.OwnerUserId == int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!))
            : await _db.Devices.AnyAsync(d => d.FieldId == field);
        if (!allowed) throw new HubException("Not allowed to view this field.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(field));
    }
}
