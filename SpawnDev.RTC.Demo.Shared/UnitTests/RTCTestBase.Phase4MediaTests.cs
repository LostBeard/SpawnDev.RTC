using SpawnDev.RTC;
using SpawnDev.RTC.Desktop;
using SpawnDev.MultiMedia;
using SpawnDev.UnitTesting;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Phase 4a - desktop audio bridge end-to-end. Feeds a synthetic 48 kHz Float32 stereo
        /// sine-wave IAudioTrack (from SpawnDev.MultiMedia) into a DesktopRTCPeerConnection via
        /// the new AddTrack(IAudioTrack) overload, negotiates with a second DesktopRTCPeerConnection
        /// over loopback DTLS/SRTP, and asserts that (1) OnTrack fires with Kind=="audio", (2) the
        /// SDP includes m=audio, (3) pc2's NativeConnection.OnAudioFrameReceived fires with
        /// non-empty encoded-payload frames within a reasonable window - PROVING that Opus-encoded
        /// audio actually traverses the RTP pipeline, not merely that signaling handshakes finish.
        /// No mocks. Real SipSorcery RTCPeerConnection, real Concentus Opus encoder, real RTP.
        /// Browser path skipped - the browser uses its native WebRTC stack, not this bridge.
        /// </summary>
        [TestMethod]
        public async Task Phase4_Desktop_AudioBridge_TrackReceived()
        {
            if (OperatingSystem.IsBrowser()) return;

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            using var audioTrack = new SineWaveAudioTrack(frequencyHz: 440.0, sampleRateHz: 48000, channels: 2);

            // Default bridge encoder advertises [Opus, PCMU, PCMA, G722] with Opus first.
            // Both peers use the same list. The offerer-priority bug in SipSorcery's
            // SortMediaCapability call (RTPSession.cs:1221 - inverted ternary) used to cause
            // pc1 to land on PCMU and pc2 on Opus from the same offer/answer; the fix in this
            // branch lines both sides up on the offerer's preferred codec (Opus).
            var desktopPc1 = (DesktopRTCPeerConnection)pc1;
            desktopPc1.AddTrack(audioTrack);

            using var sinkTrack = new SineWaveAudioTrack(frequencyHz: 0.0, sampleRateHz: 48000, channels: 2);
            var desktopPc2 = (DesktopRTCPeerConnection)pc2;
            desktopPc2.AddTrack(sinkTrack);

            pc1.CreateDataChannel("signal");
            pc2.OnDataChannel += _ => { };

            var audioTrackReceived = new TaskCompletionSource<RTCTrackEventInit>();
            pc2.OnTrack += e =>
            {
                if (e.Track.Kind == "audio") audioTrackReceived.TrySetResult(e);
            };

            // Capture what format SipSorcery actually negotiates so the diagnostic can report it
            // if the audio pipeline silently rejects frames as a format mismatch.
            string negotiatedPc1 = "(not yet fired)";
            string negotiatedPc2 = "(not yet fired)";
            string? bridgeError = null;
            desktopPc1.NativeConnection.OnAudioFormatsNegotiated += fmts =>
            {
                if (fmts == null || fmts.Count == 0) { negotiatedPc1 = "(empty list)"; return; }
                var f = fmts[0];
                negotiatedPc1 = $"{f.Codec} ClockRate={f.ClockRate} Channels={f.ChannelCount}";
            };
            desktopPc2.NativeConnection.OnAudioFormatsNegotiated += fmts =>
            {
                if (fmts == null || fmts.Count == 0) { negotiatedPc2 = "(empty list)"; return; }
                var f = fmts[0];
                negotiatedPc2 = $"{f.Codec} ClockRate={f.ClockRate} Channels={f.ChannelCount}";
            };
            // Also surface any bridge-level error (e.g. format mismatch) the main test would
            // otherwise lose silently.
            desktopPc1.AudioSources.First().OnAudioSourceError += msg => bridgeError = msg;

            // Count encoded audio frames the receiver actually pulls off the wire - proves the
            // sender's bridge encoded frames and the RTP pipeline delivered them to pc2. Collect
            // at least a few so a single spurious packet can't pass the test.
            int framesReceived = 0;
            int totalEncodedBytes = 0;
            var enoughFrames = new TaskCompletionSource<bool>();
            desktopPc2.NativeConnection.OnAudioFrameReceived += frame =>
            {
                if (frame?.EncodedAudio == null || frame.EncodedAudio.Length == 0) return;
                Interlocked.Increment(ref framesReceived);
                Interlocked.Add(ref totalEncodedBytes, frame.EncodedAudio.Length);
                if (framesReceived >= 5) enoughFrames.TrySetResult(true);
            };

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            audioTrack.Start();

            var completed = await Task.WhenAny(audioTrackReceived.Task, Task.Delay(15000));
            if (completed != audioTrackReceived.Task)
                throw new Exception("Timed out waiting for OnTrack(audio) on pc2. Audio negotiation failed through MultiMediaAudioSource bridge.");

            var trackEvent = await audioTrackReceived.Task;
            if (trackEvent.Track.Kind != "audio")
                throw new Exception($"Expected audio track, got '{trackEvent.Track.Kind}'");
            if (trackEvent.Track.ReadyState != "live")
                throw new Exception($"Expected live track, got '{trackEvent.Track.ReadyState}'");

            var localSdp = pc1.LocalDescription?.Sdp ?? "";
            if (!localSdp.Contains("m=audio"))
                throw new Exception($"pc1 local SDP missing m=audio - bridge did not add an audio track. SDP:\n{localSdp}");
            if (!localSdp.Contains("opus", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"pc1 local SDP missing opus codec - bridge is not advertising Opus as a negotiated format. SDP:\n{localSdp}");

            // Wait for encoded audio to actually flow. 20 s budget covers local ICE + DTLS
            // handshake (typically 1-3 s on loopback) plus the first few 20 ms Opus frames.
            var framesTask = await Task.WhenAny(enoughFrames.Task, Task.Delay(20000));
            if (framesTask != enoughFrames.Task)
            {
                var bridge = desktopPc1.AudioSources.FirstOrDefault();
                int bridgeEncoded = bridge?.EncodedFrameCount ?? -1;
                int bridgeBytes = bridge?.EncodedByteCount ?? -1;
                throw new Exception(
                    $"Timed out waiting for encoded audio frames on pc2. " +
                    $"Receiver: Got {framesReceived} frame(s), {totalEncodedBytes} byte(s) via OnAudioFrameReceived. " +
                    $"Bridge on sender: encoded {bridgeEncoded} frame(s), {bridgeBytes} byte(s) total via the Opus encoder. " +
                    $"Negotiated format (pc1) = {negotiatedPc1}. Negotiated format (pc2) = {negotiatedPc2}. " +
                    $"Bridge error = {bridgeError ?? "(none)"}. " +
                    $"pc1 connection state = {pc1.ConnectionState}, pc2 connection state = {pc2.ConnectionState}, pc1 ICE = {pc1.IceConnectionState}, pc2 ICE = {pc2.IceConnectionState}.");
            }

            if (framesReceived < 5)
                throw new Exception($"Expected at least 5 encoded audio frames, got {framesReceived}.");
            if (totalEncodedBytes < 5)
                throw new Exception($"Encoded audio frames were empty ({totalEncodedBytes} bytes total). Encoder is producing zero-length output.");

            audioTrack.Stop();
        }

        /// <summary>
        /// Phase 4a - bridge format rejection: asserts MultiMediaAudioSource throws a clear
        /// NotSupportedException when the input sample rate does not match the negotiated codec
        /// rate. Guards against silent-garbage audio when a future test wires a mismatched
        /// track. Rule 4b - prove unsupported paths fail loudly.
        /// </summary>
        [TestMethod]
        public async Task Phase4_Desktop_AudioBridge_RejectsSampleRateMismatch()
        {
            if (OperatingSystem.IsBrowser()) return;

            using var track = new SineWaveAudioTrack(frequencyHz: 440.0, sampleRateHz: 16000, channels: 2);

            using var source = new MultiMediaAudioSource(track);
            // Default format is Opus@48k stereo. Track is 16k stereo. Feeding a frame must throw.

            string? capturedError = null;
            source.OnAudioSourceError += msg => capturedError = msg;

            // Attach a no-op encoded-sample handler so HasEncodedAudioSubscribers is true and
            // the bridge actually processes the frame (otherwise HandleFrame early-returns).
            source.OnAudioSourceEncodedSample += (_, _) => { };

            await source.StartAudio();

            track.EmitOneFrame();

            // Give the event pump a beat.
            for (int i = 0; i < 20 && capturedError == null; i++) await Task.Delay(20);

            if (capturedError == null)
                throw new Exception("Expected MultiMediaAudioSource to surface a sample-rate-mismatch error; none raised.");
            if (!capturedError.Contains("sample rate"))
                throw new Exception($"Expected sample-rate-mismatch error message; got: {capturedError}");
        }

        /// <summary>
        /// Test-only synthetic audio track. Emits 20 ms Float32 stereo PCM frames at the
        /// configured sample rate with a constant-frequency sine wave. Stopping raises OnEnded.
        /// Not a public MultiMedia API - lives here so the Phase 4 bridge tests can exercise the
        /// pipeline without real hardware.
        /// </summary>
        private sealed class SineWaveAudioTrack : IAudioTrack
        {
            private readonly int _samplesPerChannelPerFrame;
            private readonly double _frequencyHz;
            private readonly int _channels;
            private CancellationTokenSource? _cts;
            private Task? _pumpTask;
            private long _sampleCursor;
            private bool _enabled = true;
            private string _readyState = "live";

            public string Id { get; } = Guid.NewGuid().ToString();
            public string Kind => "audio";
            public string Label { get; } = "SineWaveAudioTrack";
            public int SampleRate { get; }
            public int ChannelCount => _channels;
            public int BitsPerSample => 32;

            public bool Enabled
            {
                get => _enabled;
                set => _enabled = value;
            }

            public bool Muted => false;
            public string ReadyState => _readyState;
            public string ContentHint { get; set; } = "";

            public event Action? OnEnded;
            public event Action? OnMute;
            public event Action? OnUnmute;
            public event Action<AudioFrame>? OnFrame;

            public SineWaveAudioTrack(double frequencyHz, int sampleRateHz, int channels)
            {
                _frequencyHz = frequencyHz;
                _channels = channels;
                SampleRate = sampleRateHz;
                _samplesPerChannelPerFrame = sampleRateHz * 20 / 1000; // 20 ms frames
            }

            public void Start()
            {
                if (_pumpTask != null) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _pumpTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            EmitOneFrame();
                            await Task.Delay(20, token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }

            public void EmitOneFrame()
            {
                if (!_enabled || OnFrame == null) return;
                var byteBuf = new byte[_samplesPerChannelPerFrame * _channels * 4];
                double twoPi = 2.0 * Math.PI;
                double amp = 0.25;
                Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(byteBuf.AsSpan());
                for (int i = 0; i < _samplesPerChannelPerFrame; i++)
                {
                    double t = (_sampleCursor + i) / (double)SampleRate;
                    float s = (float)(amp * Math.Sin(twoPi * _frequencyHz * t));
                    for (int c = 0; c < _channels; c++)
                    {
                        floats[i * _channels + c] = s;
                    }
                }
                _sampleCursor += _samplesPerChannelPerFrame;
                OnFrame.Invoke(new AudioFrame(SampleRate, _channels, _samplesPerChannelPerFrame, new ReadOnlyMemory<byte>(byteBuf), _sampleCursor));
            }

            public MediaTrackSettings GetSettings() => new()
            {
                SampleRate = SampleRate,
                ChannelCount = _channels,
                SampleSize = BitsPerSample,
            };

            public SpawnDev.MultiMedia.MediaTrackConstraints GetConstraints() => new();
            public Task ApplyConstraints(SpawnDev.MultiMedia.MediaTrackConstraints constraints) => Task.CompletedTask;

            public void Stop()
            {
                if (_readyState == "ended") return;
                _readyState = "ended";
                _cts?.Cancel();
                try { _pumpTask?.Wait(500); } catch { }
                OnEnded?.Invoke();
            }

            public IMediaStreamTrack Clone() => new SineWaveAudioTrack(_frequencyHz, SampleRate, _channels);

            public void Dispose()
            {
                Stop();
                _cts?.Dispose();
                _cts = null;
                _pumpTask = null;
            }
        }

        /// <summary>
        /// Phase 4b - desktop H.264 video bridge end-to-end. Generates a synthetic NV12 pattern
        /// (moving gradient, no real camera), drives it through <see cref="MultiMediaVideoSource"/>'s
        /// ExternalVideoSourceRawSample path at 30 fps, negotiates with a second
        /// DesktopRTCPeerConnection over loopback DTLS/SRTP, and asserts:
        /// (1) pc2's OnTrack fires with Kind=="video", (2) pc1's SDP contains m=video and H264
        /// codec line, (3) the sender-side MultiMediaVideoSource encoded > 0 frames - proving
        /// the Windows MediaFoundation H.264 MFT actually produced NAL units inside the RTP
        /// pipeline. Windows-only; skipped on browsers (which use native WebRTC video).
        /// </summary>
        [TestMethod]
        public async Task Phase4b_Desktop_VideoBridge_EncodesAndNegotiatesH264()
        {
            if (OperatingSystem.IsBrowser()) return;
            if (!OperatingSystem.IsWindows()) return; // MFT is Windows-only until Phase 5

            const int Width = 320;
            const int Height = 240;
            const int Fps = 30;

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            var desktopPc1 = (DesktopRTCPeerConnection)pc1;
            var desktopPc2 = (DesktopRTCPeerConnection)pc2;

            // Build the video source directly (no IVideoTrack - we drive frames with
            // ExternalVideoSourceRawSample from the test loop).
            using var source = new StubVideoTrack(Width, Height, Fps);
            var videoSender = desktopPc1.AddTrack(source);

            // pc2 also attaches a stub so the negotiation is video-video on both sides
            // (mirrors the Phase 4a audio test's symmetric pattern).
            using var sinkSource = new StubVideoTrack(Width, Height, Fps);
            desktopPc2.AddTrack(sinkSource);

            // DataChannel to keep the PC alive through signaling (matches the audio test).
            pc1.CreateDataChannel("signal");
            pc2.OnDataChannel += _ => { };

            var videoTrackReceived = new TaskCompletionSource<RTCTrackEventInit>();
            pc2.OnTrack += e =>
            {
                if (e.Track.Kind == "video") videoTrackReceived.TrySetResult(e);
            };

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Pump frames into the sender-side source at ~30 fps for up to 20 s or until we
            // see the track event + enough encoded frames.
            var senderBridge = desktopPc1.VideoSources.First();
            var pumpCts = new CancellationTokenSource();
            var pumpTask = Task.Run(async () =>
            {
                int frameIdx = 0;
                while (!pumpCts.IsCancellationRequested)
                {
                    var nv12 = BuildMovingPatternNV12(Width, Height, frameIdx++);
                    try
                    {
                        senderBridge.ExternalVideoSourceRawSample(
                            durationMilliseconds: 33,
                            width: Width,
                            height: Height,
                            sample: nv12,
                            pixelFormat: VideoPixelFormatsEnum.NV12);
                    }
                    catch { /* bridge errors are collected via OnVideoSourceError below */ }
                    await Task.Delay(33, pumpCts.Token).ContinueWith(_ => { });
                }
            });

            string? bridgeError = null;
            senderBridge.OnVideoSourceError += msg => bridgeError = msg;

            var completed = await Task.WhenAny(videoTrackReceived.Task, Task.Delay(20000));
            if (completed != videoTrackReceived.Task)
            {
                pumpCts.Cancel();
                throw new Exception(
                    $"Timed out waiting for OnTrack(video) on pc2. Sender bridge encoded " +
                    $"{senderBridge.EncodedFrameCount} frame(s) / {senderBridge.EncodedByteCount} bytes. " +
                    $"Bridge error = {bridgeError ?? "(none)"}. " +
                    $"pc1 connection state = {pc1.ConnectionState}, pc2 = {pc2.ConnectionState}.");
            }

            var trackEvent = await videoTrackReceived.Task;
            if (trackEvent.Track.Kind != "video")
                throw new Exception($"Expected video track, got '{trackEvent.Track.Kind}'");

            var localSdp = pc1.LocalDescription?.Sdp ?? "";
            if (!localSdp.Contains("m=video"))
                throw new Exception($"pc1 local SDP missing m=video. SDP:\n{localSdp}");
            if (!localSdp.Contains("H264", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"pc1 local SDP missing H264 codec. SDP:\n{localSdp}");

            // Wait up to a few more seconds for the sender to have encoded a handful of
            // frames. The H.264 MFT emits every frame in low-latency mode so this should
            // happen nearly immediately once pumping starts.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (senderBridge.EncodedFrameCount < 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            pumpCts.Cancel();
            try { await pumpTask; } catch { }

            if (senderBridge.EncodedFrameCount < 5)
                throw new Exception(
                    $"Sender-side MultiMediaVideoSource only encoded {senderBridge.EncodedFrameCount} frame(s) " +
                    $"in ~5 s of pumping. Expected >= 5. Bridge error = {bridgeError ?? "(none)"}.");

            if (senderBridge.EncodedByteCount < 1000)
                throw new Exception(
                    $"Sender-side encoder produced only {senderBridge.EncodedByteCount} bytes. Encoder is emitting suspiciously small output.");
        }

        private static byte[] BuildMovingPatternNV12(int width, int height, int frameIndex)
        {
            int ySize = width * height;
            int uvSize = width * height / 2;
            var data = new byte[ySize + uvSize];
            int offset = (frameIndex * 8) % 256;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    data[y * width + x] = (byte)((x + y + offset) & 0xFF);
            for (int i = ySize; i < ySize + uvSize; i++) data[i] = 128;
            return data;
        }

        /// <summary>
        /// Test-only stub <see cref="IVideoTrack"/>. Does not generate frames on its own (the
        /// test pumps frames via ExternalVideoSourceRawSample); its job is to satisfy the
        /// DesktopRTCPeerConnection.AddTrack(IVideoTrack) overload and provide FrameRate +
        /// geometry metadata.
        /// </summary>
        private sealed class StubVideoTrack : IVideoTrack
        {
            public StubVideoTrack(int width, int height, double frameRate)
            {
                Width = width;
                Height = height;
                FrameRate = frameRate;
            }

            public string Id { get; } = Guid.NewGuid().ToString();
            public string Label { get; } = "stub-video";
            public string Kind => "video";
            public bool Enabled { get; set; } = true;
            public bool Muted => false;
            public string ReadyState { get; private set; } = "live";
            public string ContentHint { get; set; } = "";

            public int Width { get; }
            public int Height { get; }
            public double FrameRate { get; }
            public VideoPixelFormat Format => VideoPixelFormat.NV12;

#pragma warning disable CS0067
            public event Action<VideoFrame>? OnFrame;
            public event Action? OnMute;
            public event Action? OnUnmute;
            public event Action? OnEnded;
#pragma warning restore CS0067

            public void Stop()
            {
                if (ReadyState == "ended") return;
                ReadyState = "ended";
                OnEnded?.Invoke();
            }

            public IMediaStreamTrack Clone() => new StubVideoTrack(Width, Height, FrameRate);

            public MediaTrackSettings GetSettings() => new MediaTrackSettings
            {
                Width = Width,
                Height = Height,
                FrameRate = FrameRate,
                PixelFormat = Format,
            };

            public SpawnDev.MultiMedia.MediaTrackConstraints GetConstraints() => new SpawnDev.MultiMedia.MediaTrackConstraints();

            public Task ApplyConstraints(SpawnDev.MultiMedia.MediaTrackConstraints constraints) => Task.CompletedTask;

            public void Dispose() => Stop();
        }
    }
}
