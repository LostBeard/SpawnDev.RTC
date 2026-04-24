using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.RTC.Server;

/// <summary>
/// Generate time-limited HMAC-based TURN credentials (the Twilio / Cloudflare TURN
/// auth pattern, documented in RFC 8489 §9.2 and widely used in the WebRTC
/// ecosystem). The app backend generates a credential per authenticated user;
/// the TURN server validates without ever knowing the user identity — just the
/// shared secret.
///
/// <para><strong>Flow:</strong>
/// <list type="number">
///   <item>Your app backend and the TURN server share a secret (an env-var string,
///         32+ bytes recommended).</item>
///   <item>When an authenticated user needs TURN, your backend calls
///         <see cref="Generate(string, string, TimeSpan)"/> to mint a credential
///         pair for them.</item>
///   <item>The app includes those credentials in its
///         <c>RTCPeerConnectionConfig.IceServers</c> entry.</item>
///   <item>The TURN server validates the HMAC on Allocate. No shared user DB;
///         expiry is encoded in the username itself.</item>
/// </list>
/// </para>
///
/// <para><strong>What this buys you:</strong> Only users who can hit your
/// authenticated backend get TURN credentials. Stolen credentials expire.
/// No user list to keep synced between app backend and TURN server.
/// Industry-standard for public-internet TURN deployments.</para>
///
/// <para><strong>SipSorcery TurnServer caveat:</strong> Our wrapped
/// <see cref="SIPSorcery.Net.TurnServer"/> validates long-term credentials
/// (single static username/password pair). For per-user ephemeral credentials
/// you'd set the server's configured username/password to a sentinel and
/// validate at a wrapper layer on Allocate - not currently plumbed end-to-end.
/// This helper is provided for consumers who front their TURN with a reverse
/// proxy or wrap the validation themselves. Full ephemeral-credential
/// enforcement at the server level is a Phase 2 item (requires upstream
/// SipSorcery changes or a fork patch).</para>
/// </summary>
public static class EphemeralTurnCredentials
{
    /// <summary>
    /// Generate a TURN credential pair bound to <paramref name="userId"/> that
    /// expires <paramref name="lifetime"/> from now. The username encodes the
    /// expiry Unix timestamp + userId so the server can validate without any
    /// shared user database.
    /// </summary>
    /// <param name="sharedSecret">Shared secret between backend and TURN server.
    /// At least 32 bytes of entropy recommended. Typically an env var.</param>
    /// <param name="userId">Opaque identifier for the user (your app's user ID).
    /// Encoded in the username for audit/debugging. Can be any string that
    /// doesn't contain <c>:</c>.</param>
    /// <param name="lifetime">How long the credential is valid from now.
    /// Typical values: 1-24 hours. Short enough that a stolen credential
    /// expires quickly; long enough to survive a WebRTC session.</param>
    public static (string Username, string Password) Generate(
        string sharedSecret,
        string userId,
        TimeSpan lifetime)
    {
        if (string.IsNullOrEmpty(sharedSecret)) throw new ArgumentException("sharedSecret is required", nameof(sharedSecret));
        if (string.IsNullOrEmpty(userId)) throw new ArgumentException("userId is required", nameof(userId));
        if (userId.Contains(':')) throw new ArgumentException("userId must not contain ':'", nameof(userId));
        if (lifetime <= TimeSpan.Zero) throw new ArgumentException("lifetime must be positive", nameof(lifetime));

        var expiryUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var username = $"{expiryUnix}:{userId}";
        var password = ComputePassword(sharedSecret, username);
        return (username, password);
    }

    /// <summary>
    /// Validate a presented credential pair: the HMAC must match the shared
    /// secret AND the encoded expiry must not be past.
    /// </summary>
    /// <returns>True if the credential is valid and unexpired; false otherwise.</returns>
    public static bool Validate(string sharedSecret, string username, string password)
    {
        if (string.IsNullOrEmpty(sharedSecret) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return false;

        // Username format: "<expiry-unix>:<userId>". Parse expiry, check not past.
        var colonIdx = username.IndexOf(':');
        if (colonIdx <= 0 || colonIdx >= username.Length - 1) return false;

        var expiryStr = username.AsSpan(0, colonIdx);
        if (!long.TryParse(expiryStr, out var expiryUnix)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) < DateTimeOffset.UtcNow) return false;

        // HMAC check. Constant-time string compare to avoid timing oracles.
        var expected = ComputePassword(sharedSecret, username);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(password));
    }

    private static string ComputePassword(string sharedSecret, string username)
    {
        // Standard pattern: HMAC-SHA1(secret, username), base64-encoded. Matches
        // what Twilio / Cloudflare / coturn use for REST-API-issued credentials.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        return Convert.ToBase64String(mac);
    }

    /// <summary>
    /// Server-side resolver that produces the SipSorcery long-term-credential HMAC
    /// key (<c>MD5(username:realm:password)</c>) for an ephemeral credential issued
    /// by <see cref="Generate"/>. Designed to be passed as
    /// <c>TurnServerConfig.ResolveHmacKey</c> via a closure that captures the
    /// shared secret + realm. Returns <c>null</c> if the username format is
    /// invalid or the encoded expiry has passed, which the TURN server treats as
    /// 401 Unauthorized.
    /// </summary>
    /// <param name="sharedSecret">Same shared secret used by <see cref="Generate"/>.</param>
    /// <param name="realm">TURN server realm (must match <c>TurnServerConfig.Realm</c>).</param>
    /// <param name="username">USERNAME attribute from the incoming STUN/TURN request.</param>
    /// <returns>20-byte MD5 key, or <c>null</c> to reject with 401.</returns>
    public static byte[]? ResolveLongTermKey(string sharedSecret, string realm, string username)
    {
        if (string.IsNullOrEmpty(sharedSecret) || string.IsNullOrEmpty(realm) || string.IsNullOrEmpty(username))
            return null;

        // Username format: "<expiry-unix>:<userId>". Parse expiry, reject if past or malformed.
        var colonIdx = username.IndexOf(':');
        if (colonIdx <= 0 || colonIdx >= username.Length - 1) return null;

        var expiryStr = username.AsSpan(0, colonIdx);
        if (!long.TryParse(expiryStr, out var expiryUnix)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) < DateTimeOffset.UtcNow) return null;

        // Recompute the password the client would have: HMAC-SHA1(secret, username), base64.
        var expectedPassword = ComputePassword(sharedSecret, username);

        // Long-term credential HMAC key: MD5(username:realm:password). Matches
        // what TurnServer's constructor computes from the static Username/Password
        // pair - so MESSAGE-INTEGRITY validation is identical to the static case,
        // just with a freshly-computed per-request key.
        return MD5.HashData(
            Encoding.UTF8.GetBytes($"{username}:{realm}:{expectedPassword}"));
    }

    /// <summary>
    /// Compose a <see cref="StunTurnServerOptions.ResolveHmacKey"/> delegate that
    /// gates TURN access on tracker connectivity. Only clients whose <c>userId</c>
    /// segment of the ephemeral username is currently announced to
    /// <paramref name="tracker"/> get a valid HMAC key back; all others are
    /// rejected with 401 Unauthorized.
    /// <para>
    /// Typical deployment: the same ASP.NET Core host runs both the tracker
    /// (<see cref="TrackerSignalingServer"/>) and the TURN server; peers must
    /// WebSocket-announce before requesting TURN allocation. Protects against
    /// scraping attackers who discover the shared secret - they still can't
    /// allocate unless they maintain a live WebSocket session with a matching
    /// peerId, which costs them per-connection tracker visibility.
    /// </para>
    /// <para>
    /// The userId segment of the ephemeral username is expected to match the
    /// peerId the client announces to the tracker with.
    /// Consumers mint credentials via <see cref="Generate(string, string, TimeSpan)"/>
    /// passing their tracker peerId as the <c>userId</c> argument.
    /// </para>
    /// </summary>
    public static Func<string, byte[]?> TrackerGatedResolver(
        string sharedSecret,
        string realm,
        TrackerSignalingServer tracker)
    {
        if (string.IsNullOrEmpty(sharedSecret)) throw new ArgumentException("sharedSecret is required", nameof(sharedSecret));
        if (string.IsNullOrEmpty(realm)) throw new ArgumentException("realm is required", nameof(realm));
        ArgumentNullException.ThrowIfNull(tracker);

        return username =>
        {
            if (string.IsNullOrEmpty(username)) return null;

            // Extract the userId segment. Reject malformed usernames outright.
            var colonIdx = username.IndexOf(':');
            if (colonIdx <= 0 || colonIdx >= username.Length - 1) return null;
            var userId = username.Substring(colonIdx + 1);

            // Gate: peer must be tracker-connected. Belt-and-suspenders: even with
            // a valid HMAC + unexpired username, reject if they aren't announced.
            if (!tracker.IsPeerConnected(userId)) return null;

            return ResolveLongTermKey(sharedSecret, realm, username);
        };
    }

    /// <summary>
    /// Compose a resolver that uses a period-derived sub-secret instead of a
    /// stable shared secret. The per-request secret is
    /// <c>HMAC-SHA256(masterSecret, floor(expiryUnix / periodSeconds))</c>, so
    /// credentials only validate against the master secret indirectly - if the
    /// master leaks, only credentials whose expiry falls in still-valid periods
    /// are at risk, not the full history.
    /// <para>
    /// Both backend (credential issuance) and server (credential validation)
    /// derive the period from the <em>expiry</em> encoded in the username, so
    /// there is no period-boundary race: whichever period the expiry falls
    /// in is the period the sub-secret is derived from, deterministically.
    /// Clients must mint credentials via
    /// <see cref="GeneratePeriodic(string, string, TimeSpan, int)"/> (not the
    /// stable-secret <see cref="Generate(string, string, TimeSpan)"/>).
    /// </para>
    /// <para>
    /// Typical <paramref name="periodSeconds"/>: 3600 (1 hour) to 86400 (24 h).
    /// Shorter periods limit blast radius but increase the odds a client's
    /// freshly-issued credential is valid only briefly (until its expiry rolls
    /// into the next period - a non-issue as long as <c>lifetime</c> on
    /// <see cref="GeneratePeriodic"/> fits inside a single period).
    /// </para>
    /// </summary>
    public static Func<string, byte[]?> PeriodRotatingResolver(
        string masterSecret,
        string realm,
        int periodSeconds)
    {
        if (string.IsNullOrEmpty(masterSecret)) throw new ArgumentException("masterSecret is required", nameof(masterSecret));
        if (string.IsNullOrEmpty(realm)) throw new ArgumentException("realm is required", nameof(realm));
        if (periodSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(periodSeconds), "periodSeconds must be positive");

        return username =>
        {
            if (string.IsNullOrEmpty(username)) return null;

            var colonIdx = username.IndexOf(':');
            if (colonIdx <= 0 || colonIdx >= username.Length - 1) return null;

            var expiryStr = username.AsSpan(0, colonIdx);
            if (!long.TryParse(expiryStr, out var expiryUnix)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) < DateTimeOffset.UtcNow) return null;

            // Period-derived sub-secret. Both backend and server deterministically
            // derive the same value from (masterSecret, period) - no coordination.
            var periodId = expiryUnix / periodSeconds;
            var subSecret = DerivePeriodSecret(masterSecret, periodId);

            return ResolveLongTermKey(subSecret, realm, username);
        };
    }

    /// <summary>
    /// Credential-issuance companion for <see cref="PeriodRotatingResolver"/>.
    /// Derives the same period sub-secret the server will resolve to, then
    /// uses it as the shared secret for a standard ephemeral-credential pair.
    /// The returned credentials only validate against a server configured with
    /// a <see cref="PeriodRotatingResolver"/> using the same
    /// <paramref name="masterSecret"/> and <paramref name="periodSeconds"/>.
    /// </summary>
    /// <param name="masterSecret">Root shared secret. Stable across periods;
    /// rotate rarely / on compromise.</param>
    /// <param name="userId">Opaque user identifier (peer id, session id, etc.).
    /// Encoded in the username for audit.</param>
    /// <param name="lifetime">How long the credential is valid. SHOULD be less
    /// than <paramref name="periodSeconds"/> so the credential's expiry stays
    /// within a single period - if it crosses a boundary, the server derives
    /// a different sub-secret than the backend did and validation fails.</param>
    /// <param name="periodSeconds">Seconds between sub-secret rotations. Must
    /// match the value passed to <see cref="PeriodRotatingResolver"/> on the
    /// server side.</param>
    public static (string Username, string Password) GeneratePeriodic(
        string masterSecret,
        string userId,
        TimeSpan lifetime,
        int periodSeconds)
    {
        if (string.IsNullOrEmpty(masterSecret)) throw new ArgumentException("masterSecret is required", nameof(masterSecret));
        if (periodSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(periodSeconds), "periodSeconds must be positive");

        var expiryUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var periodId = expiryUnix / periodSeconds;
        var subSecret = DerivePeriodSecret(masterSecret, periodId);

        // Standard Generate path with the derived sub-secret.
        return Generate(subSecret, userId, lifetime);
    }

    /// <summary>
    /// Derive a period-specific sub-secret as
    /// <c>Base64(HMAC-SHA256(masterSecret, periodId))</c>. Base64 keeps the
    /// result ASCII-safe so it can flow through the same code paths that
    /// expect a string shared secret, without any new binary-secret plumbing.
    /// </summary>
    private static string DerivePeriodSecret(string masterSecret, long periodId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterSecret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(periodId.ToString()));
        return Convert.ToBase64String(mac);
    }
}
