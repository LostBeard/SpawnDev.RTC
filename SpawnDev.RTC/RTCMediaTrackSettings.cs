using System.Text.Json.Serialization;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Current settings of a media stream track.
    /// Returned by IRTCMediaStreamTrack.GetSettings().
    /// </summary>
    public class RTCMediaTrackSettings
    {
        [JsonPropertyName("width")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Height { get; set; }

        [JsonPropertyName("frameRate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FrameRate { get; set; }

        [JsonPropertyName("facingMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FacingMode { get; set; }

        [JsonPropertyName("sampleRate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SampleRate { get; set; }

        [JsonPropertyName("channelCount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ChannelCount { get; set; }

        [JsonPropertyName("echoCancellation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? EchoCancellation { get; set; }

        [JsonPropertyName("noiseSuppression")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? NoiseSuppression { get; set; }

        [JsonPropertyName("autoGainControl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? AutoGainControl { get; set; }

        [JsonPropertyName("deviceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeviceId { get; set; }

        [JsonPropertyName("groupId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupId { get; set; }
    }
}
