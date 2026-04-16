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
        /// Browser: wraps navigator.mediaDevices.getDisplayMedia.
        /// Desktop: not yet supported (requires SpawnDev.MultiMedia DXGI integration).
        /// </summary>
        public static Task<IRTCMediaStream> GetDisplayMedia(MediaStreamConstraints? constraints = null)
        {
            if (OperatingSystem.IsBrowser())
            {
                return Browser.BrowserMediaDevices.GetDisplayMedia(constraints);
            }
            else
            {
                throw new PlatformNotSupportedException(
                    "GetDisplayMedia is not yet supported on desktop. " +
                    "Desktop screen capture requires SpawnDev.MultiMedia DXGI integration.");
            }
        }

        /// <summary>
        /// Enumerates available media input and output devices.
        /// Returns an array of device info for cameras, microphones, and speakers.
        /// </summary>
        public static Task<RTCMediaDeviceInfo[]> EnumerateDevices()
        {
            if (OperatingSystem.IsBrowser())
            {
                return Browser.BrowserMediaDevices.EnumerateDevices();
            }
            else
            {
                return Desktop.DesktopMediaDevices.EnumerateDevices();
            }
        }
    }

    /// <summary>
    /// Information about a single media device (camera, microphone, speaker).
    /// Mirrors the W3C MediaDeviceInfo interface.
    /// </summary>
    public class RTCMediaDeviceInfo
    {
        /// <summary>
        /// Unique identifier for the device. Persists across sessions for the same origin.
        /// </summary>
        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = "";

        /// <summary>
        /// The kind of device: "videoinput", "audioinput", or "audiooutput".
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        /// <summary>
        /// Human-readable label for the device (e.g., "FaceTime HD Camera").
        /// May be empty if the user has not granted media permissions.
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>
        /// Group identifier - devices that share a physical device have the same groupId
        /// (e.g., a webcam with built-in mic).
        /// </summary>
        [JsonPropertyName("groupId")]
        public string GroupId { get; set; } = "";
    }

    /// <summary>
    /// Constraints for GetUserMedia / GetDisplayMedia.
    /// Audio and Video can each be set to true (default constraints) or a
    /// MediaTrackConstraints for specific settings. Null means not requested.
    /// </summary>
    public class MediaStreamConstraints
    {
        /// <summary>
        /// Audio constraint. Set to true for default audio, or a MediaTrackConstraints for specific settings.
        /// Null means no audio requested.
        /// </summary>
        [JsonPropertyName("audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MediaConstraint? Audio { get; set; }

        /// <summary>
        /// Video constraint. Set to true for default video, or a MediaTrackConstraints for specific settings.
        /// Null means no video requested.
        /// </summary>
        [JsonPropertyName("video")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MediaConstraint? Video { get; set; }
    }

    /// <summary>
    /// Represents a media constraint that can be either a boolean (true = use defaults)
    /// or a MediaTrackConstraints object for specific settings.
    /// Mirrors the W3C (boolean or MediaTrackConstraints) union type.
    /// </summary>
    public class MediaConstraint
    {
        /// <summary>
        /// When true, use default constraints. When false or null, use Constraints if set.
        /// </summary>
        public bool? BoolValue { get; private set; }

        /// <summary>
        /// Detailed constraints. Null when BoolValue is set.
        /// </summary>
        public MediaTrackConstraints? Constraints { get; private set; }

        /// <summary>
        /// Whether this represents a boolean value (true/false) vs detailed constraints.
        /// </summary>
        public bool IsBool => BoolValue.HasValue;

        private MediaConstraint() { }

        /// <summary>
        /// Create from a boolean value (true = request with defaults).
        /// </summary>
        public static implicit operator MediaConstraint(bool value) => new() { BoolValue = value };

        /// <summary>
        /// Create from detailed constraints.
        /// </summary>
        public static implicit operator MediaConstraint(MediaTrackConstraints constraints) =>
            new() { Constraints = constraints };
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
