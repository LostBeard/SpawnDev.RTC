namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCMediaStream.
    /// Simple collection of tracks for SipSorcery integration.
    /// </summary>
    public class DesktopRTCMediaStream : IRTCMediaStream
    {
        private readonly List<IRTCMediaStreamTrack> _tracks;
        private bool _disposed;

        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public bool Active => _tracks.Any(t => t.ReadyState == "live");

        public DesktopRTCMediaStream(IRTCMediaStreamTrack[] tracks)
        {
            _tracks = new List<IRTCMediaStreamTrack>(tracks);
        }

        public IRTCMediaStreamTrack[] GetTracks() => _tracks.ToArray();

        public IRTCMediaStreamTrack[] GetAudioTracks() =>
            _tracks.Where(t => t.Kind == "audio").ToArray();

        public IRTCMediaStreamTrack[] GetVideoTracks() =>
            _tracks.Where(t => t.Kind == "video").ToArray();

        public IRTCMediaStreamTrack? GetTrackById(string trackId) =>
            _tracks.FirstOrDefault(t => t.Id == trackId);

        public void AddTrack(IRTCMediaStreamTrack track) => _tracks.Add(track);

        public void RemoveTrack(IRTCMediaStreamTrack track) => _tracks.Remove(track);

        public IRTCMediaStream Clone()
        {
            return new DesktopRTCMediaStream(_tracks.Select(t => t.Clone()).ToArray());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var track in _tracks)
                track.Dispose();
            _tracks.Clear();
        }
    }
}
