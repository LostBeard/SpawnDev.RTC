using System.Text.Json.Serialization;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform media device access.
    /// Browser: wraps navigator.mediaDevices via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery media sources.
    /// </summary>
    public static class RTCMediaDevices
    {
        /// <summary>
        /// Requests access to media devices (camera, microphone).
        /// Returns a media stream with the requested tracks.
        /// </summary>
        public static Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            if (OperatingSystem.IsBrowser())
            {
                return Browser.BrowserMediaDevices.GetUserMedia(constraints);
            }
            else
            {
                return Desktop.DesktopMediaDevices.GetUserMedia(constraints);
            }
        }

        /// <summary>
        /// Requests access to screen capture.
        /// WASM only - throws PlatformNotSupportedException on desktop.
        /// </summary>
        public static Task<IRTCMediaStream> GetDisplayMedia(MediaStreamConstraints? constraints = null)
        {
            if (OperatingSystem.IsBrowser())
            {
                return Browser.BrowserMediaDevices.GetDisplayMedia(constraints);
            }
            else
            {
                throw new PlatformNotSupportedException("GetDisplayMedia is only available in Blazor WASM.");
            }
        }
    }

    /// <summary>
    /// Constraints for GetUserMedia / GetDisplayMedia.
    /// </summary>
    public class MediaStreamConstraints
    {
        /// <summary>
        /// Audio constraint. Set to true for default audio, or a MediaTrackConstraints for specific settings.
        /// Null means no audio requested.
        /// </summary>
        [JsonPropertyName("audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Audio { get; set; }

        /// <summary>
        /// Video constraint. Set to true for default video, or a MediaTrackConstraints for specific settings.
        /// Null means no video requested.
        /// </summary>
        [JsonPropertyName("video")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Video { get; set; }
    }

    /// <summary>
    /// Detailed constraints for an audio or video track.
    /// </summary>
    public class MediaTrackConstraints
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
    }
}
