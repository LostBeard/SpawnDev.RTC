namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform media stream track - a single audio or video track.
    /// Browser: wraps native MediaStreamTrack via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery MediaStreamTrack.
    /// </summary>
    public interface IRTCMediaStreamTrack : IDisposable
    {
        /// <summary>
        /// A unique identifier for this track.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// The kind of track: "audio" or "video".
        /// </summary>
        string Kind { get; }

        /// <summary>
        /// The label of the track (e.g., camera name, microphone name).
        /// </summary>
        string Label { get; }

        /// <summary>
        /// Whether the track is enabled. Disabled tracks produce silence/black frames.
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// Whether the track is muted (source is unable to provide media data).
        /// </summary>
        bool Muted { get; }

        /// <summary>
        /// The ready state of the track: "live" or "ended".
        /// </summary>
        string ReadyState { get; }

        /// <summary>
        /// Stops the track. After stopping, ReadyState becomes "ended".
        /// </summary>
        void Stop();

        /// <summary>
        /// Creates a duplicate of this track.
        /// </summary>
        IRTCMediaStreamTrack Clone();

        /// <summary>
        /// Fired when the track ends (source disconnected or Stop() called).
        /// </summary>
        event Action? OnEnded;

        /// <summary>
        /// Fired when the muted state changes.
        /// </summary>
        event Action? OnMute;

        /// <summary>
        /// Fired when the track is unmuted.
        /// </summary>
        event Action? OnUnmute;
    }
}
