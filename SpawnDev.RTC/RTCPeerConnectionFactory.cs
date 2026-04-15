namespace SpawnDev.RTC
{
    /// <summary>
    /// Factory for creating cross-platform RTCPeerConnection instances.
    /// Automatically selects the correct implementation based on the current platform:
    /// - Browser (Blazor WASM): uses native browser WebRTC via SpawnDev.BlazorJS
    /// - Desktop (.NET): uses SipSorcery WebRTC stack
    /// </summary>
    public static class RTCPeerConnectionFactory
    {
        /// <summary>
        /// Creates a new peer connection with the specified configuration.
        /// </summary>
        public static IRTCPeerConnection Create(RTCPeerConnectionConfig? config = null)
        {
            if (OperatingSystem.IsBrowser())
            {
                return new Browser.BrowserRTCPeerConnection(config);
            }
            else
            {
                return new Desktop.DesktopRTCPeerConnection(config);
            }
        }
    }
}
