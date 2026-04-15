namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform media stream - a collection of audio and/or video tracks.
    /// Browser: wraps native MediaStream via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery media stream.
    /// </summary>
    public interface IRTCMediaStream : IDisposable
    {
        /// <summary>
        /// A unique identifier for this stream.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Whether the stream is currently active (has at least one non-ended track).
        /// </summary>
        bool Active { get; }

        /// <summary>
        /// Returns all tracks in this stream.
        /// </summary>
        IRTCMediaStreamTrack[] GetTracks();

        /// <summary>
        /// Returns all audio tracks in this stream.
        /// </summary>
        IRTCMediaStreamTrack[] GetAudioTracks();

        /// <summary>
        /// Returns all video tracks in this stream.
        /// </summary>
        IRTCMediaStreamTrack[] GetVideoTracks();

        /// <summary>
        /// Returns a track by its ID, or null if not found.
        /// </summary>
        IRTCMediaStreamTrack? GetTrackById(string trackId);

        /// <summary>
        /// Adds a track to this stream.
        /// </summary>
        void AddTrack(IRTCMediaStreamTrack track);

        /// <summary>
        /// Removes a track from this stream.
        /// </summary>
        void RemoveTrack(IRTCMediaStreamTrack track);

        /// <summary>
        /// Clones this stream and all its tracks.
        /// </summary>
        IRTCMediaStream Clone();
    }
}
