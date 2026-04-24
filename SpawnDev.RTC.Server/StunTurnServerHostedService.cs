using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;

namespace SpawnDev.RTC.Server;

/// <summary>
/// ASP.NET Core hosted service that runs a STUN/TURN listener alongside the WebSocket
/// signaling tracker. Registered via
/// <c>SpawnDev.RTC.Server.Extensions.StunTurnServiceCollectionExtensions.AddRtcStunTurn</c>,
/// automatically started/stopped with the host process.
/// </summary>
/// <remarks>
/// Wraps <see cref="TurnServer"/> from our forked SipSorcery. TurnServer is dual-role:
/// answers STUN binding requests (no allocation state) and serves TURN allocation +
/// relay for clients who need the relay fallback. One UDP socket on the configured port
/// handles both.
/// </remarks>
public sealed class StunTurnServerHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<StunTurnServerHostedService> _logger;
    private readonly StunTurnServerOptions _options;
    private TurnServer? _turnServer;

    /// <summary>
    /// Access the underlying SipSorcery <see cref="TurnServer"/> for metrics /
    /// allocation inspection. Null until the service has started and options are
    /// configured to <see cref="StunTurnServerOptions.Enabled"/> = true.
    /// </summary>
    public TurnServer? TurnServer => _turnServer;

    public StunTurnServerHostedService(
        IOptions<StunTurnServerOptions> options,
        ILogger<StunTurnServerHostedService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SpawnDev.RTC STUN/TURN server is disabled (set StunTurnServerOptions.Enabled = true to turn it on).");
            return Task.CompletedTask;
        }

        var config = new TurnServerConfig
        {
            ListenAddress = _options.ListenAddress,
            Port = _options.Port,
            EnableTcp = _options.EnableTcp,
            EnableUdp = _options.EnableUdp,
            RelayAddress = _options.RelayAddress ?? _options.ListenAddress,
            Username = _options.Username,
            Password = _options.Password,
            Realm = _options.Realm,
            DefaultLifetimeSeconds = _options.DefaultLifetimeSeconds,
        };

        _turnServer = new TurnServer(config);
        _turnServer.Start();
        _logger.LogInformation(
            "SpawnDev.RTC STUN/TURN listening on {Address}:{Port} (tcp={EnableTcp}, udp={EnableUdp}, realm={Realm})",
            _options.ListenAddress, _options.Port, _options.EnableTcp, _options.EnableUdp, _options.Realm);

        if (_options.Username == "turn-user" || _options.Password == "turn-pass")
            _logger.LogWarning(
                "SpawnDev.RTC STUN/TURN is running with DEFAULT credentials. Override StunTurnServerOptions.Username and Password before exposing this to untrusted clients.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _turnServer?.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "Error stopping TurnServer."); }
        _turnServer?.Dispose();
        _turnServer = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None).AsValueTask();
    }
}

internal static class TaskExtensions
{
    public static ValueTask AsValueTask(this Task task) => task.IsCompletedSuccessfully
        ? ValueTask.CompletedTask
        : new ValueTask(task);
}
