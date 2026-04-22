using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.RTC.Server;

/// <summary>
/// Inbound wire message from a connected peer. All fields are optional; the
/// server dispatches on <see cref="Action"/>. Uses
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/> so JSON keys match the wire
/// convention (<c>info_hash</c>, <c>peer_id</c>, <c>to_peer_id</c>, etc.).
/// </summary>
internal sealed class WireMessage
{
    public string? Action { get; set; }
    public string? InfoHash { get; set; }
    public string? PeerId { get; set; }
    public string? ToPeerId { get; set; }
    public string? OfferId { get; set; }
    public string? Event { get; set; }
    public JsonElement? Offer { get; set; }
    public JsonElement? Answer { get; set; }
    public JsonElement? Offers { get; set; }
    public int? NumWant { get; set; }
    public long? Downloaded { get; set; }
    public long? Uploaded { get; set; }
    public long? Left { get; set; }
}

/// <summary>
/// Announce response sent back to the announcing peer. Peer list is
/// <c>[{ peer_id: "..." }]</c>; the actual offers/answers flow through
/// <see cref="RelayMessage"/> frames separate from this response.
/// </summary>
internal sealed class AnnounceResponse
{
    [JsonPropertyName("action")] public string Action { get; set; } = "announce";
    [JsonPropertyName("info_hash")] public string InfoHash { get; set; } = "";
    [JsonPropertyName("interval")] public int Interval { get; set; }
    [JsonPropertyName("complete")] public int Complete { get; set; }
    [JsonPropertyName("incomplete")] public int Incomplete { get; set; }

    [JsonPropertyName("peers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PeerSummary[]? Peers { get; set; }
}

internal sealed class PeerSummary
{
    [JsonPropertyName("peer_id")] public string PeerId { get; set; } = "";
}

/// <summary>
/// Relay envelope: outbound message routed from one peer to another via the
/// tracker. Used for offer forwarding, answer forwarding, and embedded-offer
/// relay within an announce. Optional fields are omitted when null.
/// </summary>
internal sealed class RelayMessage
{
    [JsonPropertyName("action")] public string Action { get; set; } = "announce";
    [JsonPropertyName("info_hash")] public string InfoHash { get; set; } = "";
    [JsonPropertyName("peer_id")] public string PeerId { get; set; } = "";

    [JsonPropertyName("offer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Offer { get; set; }

    [JsonPropertyName("answer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Answer { get; set; }

    [JsonPropertyName("offer_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfferId { get; set; }
}
