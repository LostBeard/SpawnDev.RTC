using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
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

        private void HandleToneChange(SpawnDev.BlazorJS.JSObjects.Event e) => OnToneChange?.Invoke();
    }
}
