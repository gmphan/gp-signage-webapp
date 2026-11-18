using GpSignageWebapp.Data.Repositories;
using GpSignageWebapp.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace GpSignageWebapp.Hubs;

/// <summary>
/// SignalR hub the player (Raspberry Pi kiosk browser) connects to.
/// Each display joins a group named by its slug; the server then pushes
/// "ShowSlide" only to that display's connections.
///
/// Client -> Server: JoinDisplay(slug), Heartbeat(slug)
/// Server -> Client: ShowSlide(slide)
/// </summary>
public class SignageHub : Hub
{
    /// <summary>Client method name the player listens on for slide changes.</summary>
    public const string ShowSlideMethod = "ShowSlide";

    /// <summary>Client method name the player listens on for a new playlist.</summary>
    public const string ShowPlaylistMethod = "ShowPlaylist";

    private readonly IDisplayRepository _displayRepository;
    private readonly ILogger<SignageHub> _logger;

    public SignageHub(IDisplayRepository displayRepository, ILogger<SignageHub> logger)
    {
        _displayRepository = displayRepository;
        _logger = logger;
    }

    /// <summary>
    /// Player registers itself for a display slug. Joins the display's group and
    /// is sent the currently-assigned slide (if any) so it can render immediately.
    /// </summary>
    public async Task JoinDisplay(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new HubException("A display slug is required.");
        }

        slug = slug.Trim().ToLowerInvariant();

        var display = await _displayRepository.GetBySlugAsync(slug, Context.ConnectionAborted);
        if (display is null)
        {
            throw new HubException($"Unknown display '{slug}'.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(slug), Context.ConnectionAborted);

        _logger.LogInformation(
            "Display '{Slug}' joined (connection {ConnectionId})", slug, Context.ConnectionId);

        // Push current state so the player shows the right content on connect/reload.
        // Send the display's ordered playlist; the player rotates through it locally.
        var playlist = PlaylistDto.From(display);
        await Clients.Caller.SendAsync(ShowPlaylistMethod, playlist, Context.ConnectionAborted);
    }
    /// <summary>
    /// Lightweight heartbeat so the admin view can show "online" status.
    /// </summary>
    public async Task Heartbeat(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return;
        }

        await _displayRepository.TouchLastSeenAsync(slug.Trim().ToLowerInvariant(), DateTime.UtcNow, Context.ConnectionAborted);
    }

    /// <summary>Group name for a display slug. Use this from server-side push code.</summary>
    public static string GroupFor(string slug) => $"display:{slug.Trim().ToLowerInvariant()}";
}


