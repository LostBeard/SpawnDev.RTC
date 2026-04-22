using System.Text.Json.Serialization;

namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Announce message sent to a WebTorrent-protocol tracker. Binary-string fields
/// (<c>info_hash</c>, <c>peer_id</c>, <c>offer_id</c>) use latin1 char-per-byte encoding -
/// see <see cref="BinaryJsonSerializer"/> for the escaping rules.
/// </summary>
internal sealed class TrackerAnnounceMessage
{
    [JsonPropertyName("action")] public string Action { get; set; } = "announce";
    [JsonPropertyName("info_hash")] public string InfoHash { get; set; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; set; } = "";
    [JsonPropertyName("uploaded")] public long Uploaded { get; set; }
    [JsonPropertyName("downloaded")] public long Downloaded { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("left")] public long? Left { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("event")] public string? Event { get; set; }

    [JsonPropertyName("numwant")] public int NumWant { get; set; } = 10;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("offers")] public object[]? Offers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("trackerid")] public string? TrackerId { get; set; }
}

/// <summary>Answer message routed back to the peer that made the original offer.</summary>
internal sealed class TrackerAnswerMessage
{
    [JsonPropertyName("action")] public string Action { get; set; } = "announce";
    [JsonPropertyName("info_hash")] public string InfoHash { get; set; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; set; } = "";
    [JsonPropertyName("to_peer_id")] public string ToPeerId { get; set; } = "";
    [JsonPropertyName("answer")] public object? Answer { get; set; }
    [JsonPropertyName("offer_id")] public string OfferId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("trackerid")] public string? TrackerId { get; set; }
}
