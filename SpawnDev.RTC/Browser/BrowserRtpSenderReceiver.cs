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

        public RTCRtpSendParameters GetParameters()
        {
            var native = NativeSender.GetParameters();
            return new RTCRtpSendParameters
            {
                TransactionId = native.TransactionId,
                Encodings = native.Encodings?.Select(ToRtcEncoding).ToArray(),
                Codecs = native.Codecs?.Select(c => new RTCRtpCodecInfo
                {
                    MimeType = c.MimeType,
                    ClockRate = c.ClockRate,
                    Channels = c.Channels,
                    SdpFmtpLine = c.SdpFmtpLine,
                }).ToArray(),
            };
        }

        public Task SetParameters(RTCRtpSendParameters parameters)
        {
            var native = new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpSendParameters
            {
                TransactionId = parameters.TransactionId,
                Encodings = parameters.Encodings?.Select(e => new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpEncodingParameters
                {
                    Rid = e.Rid,
                    Active = e.Active,
                    MaxBitrate = e.MaxBitrate,
                    MaxFramerate = e.MaxFramerate,
                    ScaleResolutionDownBy = e.ScaleResolutionDownBy,
                    ScalabilityMode = e.ScalabilityMode,
                    Priority = e.Priority,
                    NetworkPriority = e.NetworkPriority,
                }).ToArray(),
                // codecs/headerExtensions/rtcp round-trip through browser: we didn't modify them,
                // so we don't pass them back. getParameters + modify-only-encodings + setParameters
                // is the canonical simulcast flow; the browser keeps the untouched fields internally.
            };
            return NativeSender.SetParameters(native);
        }

        private static RTCRtpEncoding ToRtcEncoding(SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpEncodingParameters e) =>
            new RTCRtpEncoding
            {
                Rid = e.Rid,
                Active = e.Active,
                MaxBitrate = e.MaxBitrate,
                MaxFramerate = e.MaxFramerate,
                ScaleResolutionDownBy = e.ScaleResolutionDownBy,
                ScalabilityMode = e.ScalabilityMode,
                Priority = e.Priority,
                NetworkPriority = e.NetworkPriority,
            };
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
