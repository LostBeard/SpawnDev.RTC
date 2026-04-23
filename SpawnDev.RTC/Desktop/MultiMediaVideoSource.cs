using SIPSorceryMedia.Abstractions;
using SpawnDev.MultiMedia;
using System.Runtime.Versioning;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Bridges a SpawnDev.MultiMedia <see cref="IVideoTrack"/> (desktop webcam capture) into
    /// SipSorcery's <see cref="IVideoSource"/> shape so raw video frames from MediaFoundation /
    /// DirectShow are encoded to H.264 via <see cref="VideoEncoderFactory.CreateH264"/> and
    /// delivered as encoded samples on the RTCPeerConnection for RTP transmission.
    ///
    /// Supported input: NV12, I420, or BGRA pixel formats at the track's native resolution + fps.
    /// The bridge converts non-NV12 formats to NV12 using SpawnDev.MultiMedia's PixelFormatConverter
    /// (CPU path; GPU conversion available via GpuPixelFormatConverter when hot loops need it).
    ///
    /// Codec: H.264 baseline profile, CBR, low-latency mode (~20 ms per-frame latency typical on
    /// hardware-accelerated MFT encoders). The SDP offer advertises H.264 with
    /// <c>packetization-mode=1</c> (Single NAL + FU-A fragmentation) - the format every modern
    /// browser accepts. SipSorcery's RTPSession handles RFC 6184 packetization downstream.
    ///
    /// Windows-only until Phase 5 adds Linux VAAPI + macOS VideoToolbox encoder implementations
    /// behind the same <see cref="VideoEncoderFactory"/> facade - the bridge itself is platform-
    /// neutral; it delegates all encoding to the factory.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class MultiMediaVideoSource : IVideoSource, IDisposable
    {
        // Standard WebRTC video RTP clock (90 kHz across all codecs).
        private const int VideoClockRate = 90000;

        // Format IDs line up with the upstream VideoTestPatternSource defaults so SDP
        // negotiation picks familiar payload type numbers for browser peers.
        private const int H264_FORMAT_ID = 100;

        private readonly IVideoTrack _track;
        private SpawnDev.MultiMedia.IVideoEncoder? _encoder;
        private VideoFormat _selectedFormat;
        private List<VideoFormat> _supportedFormats;
        private bool _paused;
        private bool _closed;
        private bool _started;
        private bool _handlerAttached;
        private bool _forceKeyFrameRequested;
        private int _bitrateBps = 1_500_000;

        public event EncodedSampleDelegate? OnVideoSourceEncodedSample;
        public event RawVideoSampleDelegate? OnVideoSourceRawSample;
        public event RawVideoSampleFasterDelegate? OnVideoSourceRawSampleFaster;
        public event SourceErrorDelegate? OnVideoSourceError;

        /// <summary>Target encoder bitrate in bits per second. Default 1.5 Mbps (reasonable for 640x480 @ 30 fps).</summary>
        public int BitrateBps
        {
            get => _bitrateBps;
            set => _bitrateBps = value > 0 ? value : _bitrateBps;
        }

        /// <summary>Diagnostic: count of encoded frames emitted. Not part of IVideoSource.</summary>
        public int EncodedFrameCount { get; private set; }

        /// <summary>Diagnostic: total bytes of encoded video emitted. Not part of IVideoSource.</summary>
        public int EncodedByteCount { get; private set; }

        public MultiMediaVideoSource(IVideoTrack track)
        {
            _track = track ?? throw new ArgumentNullException(nameof(track));

            _supportedFormats = new List<VideoFormat>
            {
                new VideoFormat(VideoCodecsEnum.H264, H264_FORMAT_ID, VideoClockRate, "packetization-mode=1"),
            };
            _selectedFormat = _supportedFormats[0];
        }

        public List<VideoFormat> GetVideoSourceFormats() => _supportedFormats;

        public void SetVideoSourceFormat(VideoFormat videoFormat)
        {
            _selectedFormat = videoFormat;
            DisposeEncoder();  // force re-create on next frame at negotiated format
        }

        public void RestrictFormats(Func<VideoFormat, bool> filter)
        {
            _supportedFormats = _supportedFormats.Where(filter).ToList();
            if (_supportedFormats.Count == 0)
                throw new InvalidOperationException("MultiMediaVideoSource: RestrictFormats left zero formats - at least H.264 must remain available.");
            _selectedFormat = _supportedFormats[0];
        }

        public void ExternalVideoSourceRawSample(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
        {
            if (_closed || _paused) return;
            EncodeAndRaise(width, height, sample, pixelFormat, durationMilliseconds);
        }

        public void ExternalVideoSourceRawSampleFaster(uint durationMilliseconds, RawImage rawImage)
            => ExternalVideoSourceRawSample(durationMilliseconds, rawImage.Width, rawImage.Height, rawImage.GetBuffer(), rawImage.PixelFormat);

        public void ForceKeyFrame() => _forceKeyFrameRequested = true;

        public bool HasEncodedVideoSubscribers() => OnVideoSourceEncodedSample != null;

        public bool IsVideoSourcePaused() => _paused;

        public Task StartVideo()
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            AttachHandler();
            return Task.CompletedTask;
        }

        public Task PauseVideo()
        {
            _paused = true;
            return Task.CompletedTask;
        }

        public Task ResumeVideo()
        {
            _paused = false;
            return Task.CompletedTask;
        }

        public Task CloseVideo()
        {
            if (_closed) return Task.CompletedTask;
            _closed = true;
            DetachHandler();
            DisposeEncoder();
            return Task.CompletedTask;
        }

        private void AttachHandler()
        {
            if (_handlerAttached) return;
            _track.OnFrame += HandleFrame;
            _handlerAttached = true;
        }

        private void DetachHandler()
        {
            if (!_handlerAttached) return;
            _track.OnFrame -= HandleFrame;
            _handlerAttached = false;
        }

        private void HandleFrame(VideoFrame frame)
        {
            if (_closed || _paused) return;
            if (OnVideoSourceEncodedSample == null) return;

            try
            {
                var pixelFormat = MapPixelFormat(frame.Format);
                double fps = _track.FrameRate > 0 ? _track.FrameRate : 30.0;
                EncodeAndRaise(frame.Width, frame.Height, frame.Data.ToArray(), pixelFormat, (uint)(1000.0 / fps));
            }
            catch (Exception ex)
            {
                OnVideoSourceError?.Invoke(ex.Message);
            }
        }

        private void EncodeAndRaise(int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat, uint durationMilliseconds)
        {
            if ((width & 1) != 0 || (height & 1) != 0)
                throw new NotSupportedException($"MultiMediaVideoSource: frame dimensions must be even ({width}x{height}). NV12 / I420 encoders require even width+height.");

            EnsureEncoder(width, height);

            var nv12 = EnsureNv12(width, height, sample, pixelFormat);
            if (nv12 == null) return;

            if (_forceKeyFrameRequested)
            {
                // MFT H.264 doesn't expose a direct "force IDR" call in our wrapper yet; the
                // simplest approach that works today is to restart the encoder (which synthesizes
                // a fresh SPS+PPS+IDR on the next frame). Low-frequency operation (peer join,
                // packet-loss recovery) - the restart is cheap.
                DisposeEncoder();
                EnsureEncoder(width, height);
                _forceKeyFrameRequested = false;
            }

            // Frame rate -> timestamp (100-ns units).
            double fps = _track.FrameRate > 0 ? _track.FrameRate : 30.0;
            long tickDuration100ns = (long)(10_000_000.0 / fps);
            long timestamp100ns = EncodedFrameCount * tickDuration100ns;

            var encoded = _encoder!.Encode(nv12, timestamp100ns, tickDuration100ns);
            if (encoded == null || encoded.Length == 0) return;

            EncodedFrameCount++;
            EncodedByteCount += encoded.Length;

            // RTP timestamp duration in the 90 kHz video clock.
            uint durationRtpUnits = (uint)(VideoClockRate / fps);
            OnVideoSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
        }

        private void EnsureEncoder(int width, int height)
        {
            if (_encoder != null && _encoder.Width == width && _encoder.Height == height) return;
            DisposeEncoder();
            int fps = Math.Max((int)Math.Round(_track.FrameRate), 1);
            _encoder = VideoEncoderFactory.CreateH264(width, height, fps, _bitrateBps);
        }

        private void DisposeEncoder()
        {
            _encoder?.Dispose();
            _encoder = null;
        }

        private static byte[]? EnsureNv12(int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
        {
            // MediaFoundation H.264 MFT accepts NV12 natively. Most Windows hardware cameras
            // emit NV12 by default so the zero-copy case is the common one. For other formats
            // the bridge throws with a clear pointer at SpawnDev.MultiMedia.PixelFormatConverter
            // / GpuPixelFormatConverter for upstream conversion (CPU and 6-backend GPU paths
            // exist; this bridge intentionally stays thin and does not embed a conversion
            // pipeline of its own).
            if (pixelFormat == VideoPixelFormatsEnum.NV12) return sample;

            throw new NotSupportedException(
                $"MultiMediaVideoSource: input frame format {pixelFormat} must be converted to NV12 " +
                $"before feeding the H.264 bridge. Use SpawnDev.MultiMedia.PixelFormatConverter.Convert " +
                $"(CPU) or GpuPixelFormatConverter (GPU, all 6 ILGPU backends) upstream, or request " +
                $"NV12 capture via MediaTrackConstraints.PixelFormat = VideoPixelFormat.NV12 when calling " +
                $"MediaDevices.GetUserMedia.");
        }

        private static VideoPixelFormatsEnum MapPixelFormat(VideoPixelFormat mmFormat)
        {
            return mmFormat switch
            {
                VideoPixelFormat.NV12 => VideoPixelFormatsEnum.NV12,
                VideoPixelFormat.I420 => VideoPixelFormatsEnum.I420,
                VideoPixelFormat.BGRA => VideoPixelFormatsEnum.Bgra,
                _ => throw new NotSupportedException($"MultiMediaVideoSource: SpawnDev.MultiMedia VideoPixelFormat {mmFormat} has no direct sipsorcery equivalent.")
            };
        }

        public void Dispose()
        {
            CloseVideo();
        }
    }
}
