using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Serializes tracker wire messages to JSON with binary strings written as raw UTF-8 bytes
/// (not escaped as <c>\u00XX</c> for C1 control chars 0x80-0x9F). Matches JS
/// <c>JSON.stringify</c> output so messages are byte-identical to what
/// <c>bittorrent-tracker</c> produces.
/// </summary>
internal static class BinaryJsonSerializer
{
    // Explicit TypeInfoResolver so serialization works under file-based `dotnet run script.cs`
    // (which disables reflection-based metadata by default) and AOT/trimmed publishes. Without
    // this the announce throws InvalidOperationException "Reflection-based serialization has
    // been disabled for this application." as soon as the first tracker announce fires.
    private static readonly JsonSerializerOptions _baseOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static string Serialize(object value, JsonSerializerOptions? baseOptions = null)
    {
        var opts = baseOptions ?? _baseOpts;
        var json = JsonSerializer.Serialize(value, opts);

        var sb = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            if (i + 5 < json.Length && json[i] == '\\' && json[i + 1] == 'u' && json[i + 2] == '0' && json[i + 3] == '0')
            {
                var hex = json.Substring(i + 4, 2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int val) && val >= 0x80)
                {
                    sb.Append((char)val);
                    i += 5;
                    continue;
                }
            }
            sb.Append(json[i]);
        }
        return sb.ToString();
    }
}
