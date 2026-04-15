using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCRtpSender.
    /// </summary>
    public class BrowserRtpSender : IRTCRtpSender
    {
        public RTCRtpSender NativeSender { get; }

        public IRTCMediaStreamTrack? Track
        {
            get
            {
                var track = NativeSender.Track;
                return track == null ? null : new BrowserRTCMediaStreamTrack(track);
            }
        }

        public IRTCDTMFSender? DTMF
        {
            get
            {
                var dtmf = NativeSender.DTMF;
                return dtmf == null ? null : new BrowserRTCDTMFSender(dtmf);
            }
        }

        public BrowserRtpSender(RTCRtpSender sender)
        {
            NativeSender = sender;
        }

        public async Task ReplaceTrack(IRTCMediaStreamTrack? track)
        {
            if (track is BrowserRTCMediaStreamTrack browserTrack)
            {
                await NativeSender.ReplaceTrack(browserTrack.NativeTrack);
            }
            else if (track == null)
            {
                await NativeSender.ReplaceTrack(null!);
            }
            else
            {
                throw new ArgumentException("Track must be a BrowserRTCMediaStreamTrack in WASM.");
            }
        }

        public async Task<IRTCStatsReport> GetStats()
        {
            var report = await NativeSender.GetStats();
            return new BrowserRTCStatsReport(report);
        }

        public void SetStreams(params IRTCMediaStream[] streams)
        {
            var jsStreams = streams.Cast<BrowserRTCMediaStream>().Select(s => s.NativeStream).ToArray();
            if (jsStreams.Length > 0)
                NativeSender.SetStreams(jsStreams);
            else
                NativeSender.SetStreams();
        }
    }

    /// <summary>
    /// Browser implementation of IRTCRtpReceiver.
    /// </summary>
    public class BrowserRtpReceiver : IRTCRtpReceiver
    {
        public RTCRtpReceiver NativeReceiver { get; }

        public IRTCMediaStreamTrack Track => new BrowserRTCMediaStreamTrack(NativeReceiver.Track);

        public BrowserRtpReceiver(RTCRtpReceiver receiver)
        {
            NativeReceiver = receiver;
        }
    }
}
