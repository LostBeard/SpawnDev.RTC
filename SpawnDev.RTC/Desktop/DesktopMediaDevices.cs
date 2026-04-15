namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of media device access.
    /// Uses SipSorcery media sources.
    /// </summary>
    public static class DesktopMediaDevices
    {
        public static Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            // TODO: Implement using SipSorcery media sources
            // SipSorcery supports audio/video capture through SIPSorceryMedia packages
            throw new NotImplementedException("Desktop GetUserMedia is not yet implemented. Use SipSorcery media sources directly via NativeConnection.");
        }
    }
}
