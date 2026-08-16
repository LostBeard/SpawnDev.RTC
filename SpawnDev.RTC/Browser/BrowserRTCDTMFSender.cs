using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public class BrowserRTCDTMFSender : IRTCDTMFSender
    {
        public RTCDTMFSender NativeSender { get; }

        public string ToneBuffer => NativeSender.ToneBuffer;

        public event Action? OnToneChange;

        public BrowserRTCDTMFSender(RTCDTMFSender sender)
        {
            NativeSender = sender;
            NativeSender.OnToneChange += HandleToneChange;
        }

        public void InsertDTMF(string tones, int duration = 100, int interToneGap = 70)
        {
            NativeSender.InsertDTMF(tones, duration, interToneGap);
        }

        private void HandleToneChange(SpawnDev.SpawnJS.JSObjects.Event e) => OnToneChange?.Invoke();
    }
}
