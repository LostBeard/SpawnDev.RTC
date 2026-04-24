using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SpawnDev.RTC.Server.Extensions;

/// <summary>
/// DI wire-up helpers for the STUN/TURN server hosted service.
/// </summary>
public static class StunTurnServiceCollectionExtensions
{
    /// <summary>
    /// Register the SpawnDev.RTC STUN/TURN server as an ASP.NET Core hosted service.
    /// Starts with the host process, stops with it. Default configuration is
    /// <see cref="StunTurnServerOptions.Enabled"/> = <c>false</c> - callers MUST
    /// set <c>Enabled = true</c> in the <paramref name="configure"/> callback (or bind
    /// to a config section where <c>Enabled</c> is set) to actually open the listener.
    /// </summary>
    /// <example>
    /// <code>
    /// // One-line opt-in:
    /// builder.Services.AddRtcStunTurn(opts =>
    /// {
    ///     opts.Enabled = true;
    ///     opts.Port = 3478;
    ///     opts.Username = builder.Configuration["Turn:Username"]!;
    ///     opts.Password = builder.Configuration["Turn:Password"]!;
    ///     opts.RelayAddress = IPAddress.Parse(builder.Configuration["Turn:PublicIp"]!);
    /// });
    ///
    /// // Or via appsettings.json binding:
    /// builder.Services.AddRtcStunTurn(builder.Configuration.GetSection("Turn"));
    /// </code>
    /// </example>
    public static IServiceCollection AddRtcStunTurn(
        this IServiceCollection services,
        Action<StunTurnServerOptions>? configure = null)
    {
        services.AddOptions<StunTurnServerOptions>();
        if (configure != null) services.Configure(configure);
        services.AddSingleton<StunTurnServerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<StunTurnServerHostedService>());
        return services;
    }

    /// <summary>
    /// Register the SpawnDev.RTC STUN/TURN server, binding its options from a config
    /// section. Useful for <c>appsettings.json</c>-driven deployments.
    /// </summary>
    public static IServiceCollection AddRtcStunTurn(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configSection)
    {
        services.AddOptions<StunTurnServerOptions>().Bind(configSection);
        services.AddSingleton<StunTurnServerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<StunTurnServerHostedService>());
        return services;
    }
}
