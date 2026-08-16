using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCMediaStream.
    /// Wraps the native browser MediaStream via SpawnDev.SpawnJS.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public class BrowserRTCMediaStream : IRTCMediaStream
    {
        /// <summary>
        /// Direct access to the underlying MediaStream JS object.
        /// </summary>
        public MediaStream NativeStream { get; }

        private bool _disposed;

        public string Id => NativeStream.Id;
        public bool Active => NativeStream.Active;

        public event Action<IRTCMediaStreamTrack>? OnAddTrack;
        public event Action<IRTCMediaStreamTrack>? OnRemoveTrack;

        public BrowserRTCMediaStream(MediaStream stream)
        {
            NativeStream = stream;
        }

        public IRTCMediaStreamTrack[] GetTracks()
        {
            // SpawnJS getTracks() returns a JS Array<MediaStreamTrack>; ToArray<T>() marshals it to a .NET array.
            using var tracks = NativeStream.GetTracks();
            return tracks.ToArray<MediaStreamTrack>().Select(t => (IRTCMediaStreamTrack)new BrowserRTCMediaStreamTrack(t)).ToArray();
        }

        public IRTCMediaStreamTrack[] GetAudioTracks()
        {
            using var tracks = NativeStream.GetAudioTracks();
            return tracks.ToArray<MediaStreamTrack>().Select(t => (IRTCMediaStreamTrack)new BrowserRTCMediaStreamTrack(t)).ToArray();
        }

        public IRTCMediaStreamTrack[] GetVideoTracks()
        {
            using var tracks = NativeStream.GetVideoTracks();
            return tracks.ToArray<MediaStreamTrack>().Select(t => (IRTCMediaStreamTrack)new BrowserRTCMediaStreamTrack(t)).ToArray();
        }

        public IRTCMediaStreamTrack? GetTrackById(string trackId)
        {
            var track = NativeStream.GetTrackById(trackId);
            return track == null ? null : new BrowserRTCMediaStreamTrack(track);
        }

        public void AddTrack(IRTCMediaStreamTrack track)
        {
            if (track is BrowserRTCMediaStreamTrack browserTrack)
            {
                NativeStream.AddTrack(browserTrack.NativeTrack);
                OnAddTrack?.Invoke(track);
            }
            else
            {
                throw new ArgumentException("Track must be a BrowserRTCMediaStreamTrack in WASM.");
            }
        }

        public void RemoveTrack(IRTCMediaStreamTrack track)
        {
            if (track is BrowserRTCMediaStreamTrack browserTrack)
            {
                NativeStream.RemoveTrack(browserTrack.NativeTrack);
                OnRemoveTrack?.Invoke(track);
            }
        }

        public IRTCMediaStream Clone()
        {
            return new BrowserRTCMediaStream(NativeStream.Clone());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Stop all tracks before disposing the stream
            using var tracks = NativeStream.GetTracks();
            foreach (var track in tracks.ToArray<MediaStreamTrack>())
            {
                track.Stop();
                track.Dispose();
            }
            NativeStream.Dispose();
        }
    }
}
