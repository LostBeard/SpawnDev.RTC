using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.RTC.Signaling;

/// <summary>
/// A 20-byte room identifier for WebRTC signaling. Wire-compatible with the WebTorrent
/// tracker protocol info_hash so any public tracker can be used as a signaling relay.
/// Non-torrent consumers should prefer <see cref="FromString(string)"/>, <see cref="FromGuid(Guid)"/>,
/// or <see cref="Random"/> to obtain a key. Value type with value equality.
/// </summary>
public readonly struct RoomKey : IEquatable<RoomKey>
{
    /// <summary>Length of the room key in bytes. Always 20.</summary>
    public const int Length = 20;

    private static readonly byte[] _zero = new byte[Length];
    private readonly byte[]? _bytes;

    private RoomKey(byte[] bytes) { _bytes = bytes; }

    /// <summary>Construct from a raw 20-byte key. Defensive copy of the input.</summary>
    public static RoomKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
            throw new ArgumentException($"Room key must be exactly {Length} bytes, got {bytes.Length}.", nameof(bytes));
        return new RoomKey(bytes.ToArray());
    }

    /// <summary>
    /// Construct from a human-readable room name. Hashes the UTF-8 bytes of the name with SHA-1
    /// to produce a 20-byte key. No normalization is applied - pass <c>name.ToLowerInvariant()</c>
    /// yourself if you want case-insensitive rooms.
    /// </summary>
    public static RoomKey FromString(string roomName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        return new RoomKey(SHA1.HashData(Encoding.UTF8.GetBytes(roomName)));
    }

    /// <summary>
    /// Construct from a Guid. The 16 Guid bytes fill the first 16 bytes of the key;
    /// the trailing 4 bytes are zero. Round-trippable if the caller keeps the Guid.
    /// </summary>
    public static RoomKey FromGuid(Guid roomGuid)
    {
        var buf = new byte[Length];
        roomGuid.TryWriteBytes(buf.AsSpan(0, 16));
        return new RoomKey(buf);
    }

    /// <summary>Generate a cryptographically random 20-byte key.</summary>
    public static RoomKey Random()
    {
        var buf = new byte[Length];
        RandomNumberGenerator.Fill(buf);
        return new RoomKey(buf);
    }

    /// <summary>Parse a 40-character lowercase or uppercase hex string. Returns false on invalid input.</summary>
    public static bool TryParse(string? hex, out RoomKey key)
    {
        key = default;
        if (hex is null || hex.Length != Length * 2) return false;
        try
        {
            var buf = Convert.FromHexString(hex);
            if (buf.Length != Length) return false;
            key = new RoomKey(buf);
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>True when this is the default struct value (never initialized via a factory).</summary>
    public bool IsDefault => _bytes is null;

    /// <summary>Read-only view of the 20 key bytes. Returns 20 zero bytes for a default key.</summary>
    public ReadOnlySpan<byte> AsSpan() => _bytes ?? _zero;

    /// <summary>Defensive copy of the 20 key bytes.</summary>
    public byte[] ToArray()
    {
        var copy = new byte[Length];
        AsSpan().CopyTo(copy);
        return copy;
    }

    /// <summary>Lowercase 40-character hex representation.</summary>
    public string ToHex() => Convert.ToHexString(AsSpan()).ToLowerInvariant();

    /// <summary>
    /// Wire-format binary string used by the WebTorrent tracker protocol:
    /// each byte becomes a single latin1 char. Matches JS <c>hex2bin</c> /
    /// <c>String.fromCharCode</c> encoding used by bittorrent-tracker.
    /// </summary>
    public string ToWireString()
    {
        var src = _bytes ?? _zero;
        return string.Create(Length, src, static (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = (char)bytes[i];
        });
    }

    /// <summary>Value equality by key bytes. Default is equivalent to an all-zero key.</summary>
    public bool Equals(RoomKey other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is RoomKey k && Equals(k);

    public override int GetHashCode() => BitConverter.ToInt32(AsSpan());

    public override string ToString() => ToHex();

    public static bool operator ==(RoomKey a, RoomKey b) => a.Equals(b);
    public static bool operator !=(RoomKey a, RoomKey b) => !a.Equals(b);
}
