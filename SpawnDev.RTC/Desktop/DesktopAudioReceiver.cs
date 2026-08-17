using System;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SpawnDev.RTC.Audio;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop <see cref="IRTCAudioReceiver"/>: taps the SipSorcery peer connection's raw RTP receive
    /// path, Opus-decodes each audio payload with an <see cref="AudioEncoder"/>, and raises the PCM.
    /// The decode format follows SDP negotiation (Opus WebRTC = 48 kHz) so the samples match what the
    /// remote actually sent.
    /// </summary>
    public sealed class DesktopAudioReceiver : IRTCAudioReceiver
    {
        private readonly RTCPeerConnection _pc;
        private readonly AudioEncoder _encoder;
        private AudioFormat _format;
        private bool _started;
        private bool _disposed;

        public event Action<AudioPcmFrame>? OnPcmFrame;

        public DesktopAudioReceiver(DesktopRTCPeerConnection pc)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            _pc = pc.NativeConnection;
            // Opus-capable decoder; the exact format is pinned once SDP negotiation reports it.
            _encoder = new AudioEncoder(AudioCommonlyUsedFormats.OpusWebRTC);
            _format = _encoder.SupportedFormats[0];
            _pc.OnAudioFormatsNegotiated += OnFormatsNegotiated;
        }

        private void OnFormatsNegotiated(System.Collections.Generic.List<AudioFormat> formats)
        {
            if (formats != null && formats.Count > 0) _format = formats[0];
        }

        public void Start()
        {
            if (_disposed || _started) return;
            _started = true;
            _pc.OnRtpPacketReceived += HandleRtp;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            _pc.OnRtpPacketReceived -= HandleRtp;
        }

        private void HandleRtp(IPEndPoint endpoint, SDPMediaTypesEnum media, RTPPacket packet)
        {
            if (!_started || media != SDPMediaTypesEnum.audio) return;
            var payload = packet?.Payload;
            if (payload == null || payload.Length == 0) return;
            try
            {
                var pcm = _encoder.DecodeAudio(payload, _format);
                if (pcm == null || pcm.Length == 0) return;
                int channels = _format.ChannelCount > 0 ? _format.ChannelCount : 1;
                OnPcmFrame?.Invoke(new AudioPcmFrame(pcm, _format.ClockRate, channels));
            }
            catch { /* a lost or malformed packet must not kill the stream */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _pc.OnAudioFormatsNegotiated -= OnFormatsNegotiated;
            _encoder.Dispose();
        }
    }
}
