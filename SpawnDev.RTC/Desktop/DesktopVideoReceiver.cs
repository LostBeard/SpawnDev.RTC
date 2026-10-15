using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SpawnDev.MultiMedia;
using SpawnDev.RTC.Video;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop <see cref="IRTCVideoReceiver"/>: SipSorcery depacketizes the RTP video into whole access units; each is decoded by <see cref="VideoDecoderFactory"/>'s H.264
    /// decoder (Media Foundation on Windows) and the pictures that fall inside <see cref="MaxFramesPerSecond"/> are converted to BGRA.
    /// </summary>
    public sealed class DesktopVideoReceiver : IRTCVideoReceiver
    {
        private readonly RTCPeerConnection _pc;
        private IVideoDecoder? _decoder;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastDeliveredMs = long.MinValue / 2;
        private bool _started;
        private bool _disposed;
        private int _errors;

        public event Action<RTCVideoFrame>? OnFrame;
        public event Action<string>? OnError;

        public double MaxFramesPerSecond { get; set; } = 5;

        public DesktopVideoReceiver(DesktopRTCPeerConnection pc)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            _pc = pc.NativeConnection;
        }

        public void Start()
        {
            if (_disposed || _started) return;
            _started = true;
            _pc.OnVideoFrameReceived += HandleFrame;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _pc.OnVideoFrameReceived -= HandleFrame;
        }

        private void HandleFrame(IPEndPoint endpoint, uint rtpTimestamp, byte[] accessUnit, VideoFormat format)
        {
            if (!_started || accessUnit == null || accessUnit.Length == 0) return;
            if (format.Codec != VideoCodecsEnum.H264)
            {
                if (_errors++ == 0) OnError?.Invoke($"video codec {format.Codec} is not supported by the desktop receiver (H.264 only)");
                return;
            }
            try
            {
                IReadOnlyList<VideoFrame> decoded;
                lock (_gate)
                {
                    _decoder ??= VideoDecoderFactory.CreateH264();
                    decoded = _decoder.Decode(accessUnit, rtpTimestamp * 10000L / 90);   // 90 kHz RTP clock -> 100 ns
                }
                foreach (var picture in decoded)
                {
                    var now = _clock.ElapsedMilliseconds;
                    var interval = MaxFramesPerSecond > 0 ? 1000.0 / MaxFramesPerSecond : 0;
                    if (now - _lastDeliveredMs < interval) continue;
                    _lastDeliveredMs = now;
                    var bgra = new byte[picture.Width * picture.Height * 4];
                    PixelFormatConverter.NV12toBGRA(picture.Data.Span, bgra, picture.Width, picture.Height);
                    OnFrame?.Invoke(new RTCVideoFrame(bgra, picture.Width, picture.Height, now));
                }
            }
            catch (Exception e)
            {
                // A frame the decoder rejects (a keyframe was lost on the way) must not end the stream; the next keyframe recovers it.
                if (_errors++ < 3) OnError?.Invoke("video frame not decoded: " + e.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            lock (_gate) { _decoder?.Dispose(); _decoder = null; }
        }
    }
}
