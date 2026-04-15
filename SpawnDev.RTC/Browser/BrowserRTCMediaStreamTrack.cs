using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCMediaStreamTrack.
    /// Wraps the native browser MediaStreamTrack via SpawnDev.BlazorJS.
    /// </summary>
    public class BrowserRTCMediaStreamTrack : IRTCMediaStreamTrack
    {
        /// <summary>
        /// Direct access to the underlying BlazorJS MediaStreamTrack JSObject.
        /// </summary>
        public MediaStreamTrack NativeTrack { get; }

        private bool _disposed;

        public string Id => NativeTrack.Id;
        public string Kind => NativeTrack.Kind;
        public string Label => NativeTrack.Label;
        public bool Enabled { get => NativeTrack.Enabled; set => NativeTrack.Enabled = value; }
        public bool Muted => NativeTrack.Muted;
        public string ReadyState => NativeTrack.ReadyState;

        public event Action? OnEnded;
        public event Action? OnMute;
        public event Action? OnUnmute;

        public BrowserRTCMediaStreamTrack(MediaStreamTrack track)
        {
            NativeTrack = track;
            NativeTrack.OnEnded += HandleEnded;
            NativeTrack.OnMute += HandleMute;
        }

        public void Stop() => NativeTrack.Stop();

        public IRTCMediaStreamTrack Clone()
        {
            return new BrowserRTCMediaStreamTrack(NativeTrack.Clone());
        }

        private void HandleEnded(Event e) => OnEnded?.Invoke();
        private void HandleMute(Event e) => OnMute?.Invoke();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeTrack.OnEnded -= HandleEnded;
            NativeTrack.OnMute -= HandleMute;
            NativeTrack.Dispose();
        }
    }
}
