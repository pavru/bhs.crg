using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BHS.CRG.Api.Hubs;

[Authorize]
public class GenerationHub : Hub
{
    public async Task JoinInstance(string instanceId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"instance-{instanceId}");

    public async Task LeaveInstance(string instanceId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"instance-{instanceId}");
}
