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

        // Cache the most recent native getParameters result so SetParameters can
        // round-trip codecs / headerExtensions / rtcp back to the browser unchanged.
        // W3C spec: setParameters requires every member of RTCRtpSendParameters to
        // be present (browsers throw "Required member is undefined" otherwise). The
        // cross-platform DTO only exposes the mutable fields (TransactionId +
        // Encodings + readable Codecs) so we need the native object to provide the
        // other fields on set.
        private SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpSendParameters? _lastNativeParameters;

        public RTCRtpSendParameters GetParameters()
        {
            var native = NativeSender.GetParameters();
            _lastNativeParameters = native;
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
            // Must pass back the native object from the last getParameters with only
            // the mutable fields changed. Rebuilding a fresh native object without
            // codecs/headerExtensions/rtcp fails browser validation.
            var native = _lastNativeParameters
                ?? throw new InvalidOperationException(
                    "SetParameters must be called after GetParameters. The browser's "
                    + "setParameters requires the full parameters object including codecs, "
                    + "headerExtensions, and rtcp - the only way to obtain those is via "
                    + "getParameters first.");

            native.TransactionId = parameters.TransactionId;
            native.Encodings = parameters.Encodings?.Select(e => new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpEncodingParameters
            {
                Rid = e.Rid,
                Active = e.Active,
                MaxBitrate = e.MaxBitrate,
                MaxFramerate = e.MaxFramerate,
                ScaleResolutionDownBy = e.ScaleResolutionDownBy,
                ScalabilityMode = e.ScalabilityMode,
                Priority = e.Priority,
                NetworkPriority = e.NetworkPriority,
            }).ToArray();
            // Codecs / HeaderExtensions / Rtcp remain as returned by getParameters -
            // the browser rejects modification of those via setParameters anyway.
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
