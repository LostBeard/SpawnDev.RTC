using System.Text.RegularExpressions;
using SpawnDev.RTC;
using SpawnDev.RTC.Audio;
using SpawnDev.RTC.Desktop;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// A source that starts sending BEFORE the remote description is applied must still send with the payload type the peer negotiated.
        /// Found live (2026-10-15) on a Reachy Mini robot: its offer uses Opus as payload type 100, our local default is 111. A speech streamer that sends continuous
        /// silence from the moment of connection made <c>AudioStream.SendAudio</c> pick its sending format with no remote track yet, remember the LOCAL 111 for good, and
        /// the robot's decoder dropped every packet - no pad, no playback pipeline, no sound. Real peer connections, real Opus, real RTP; the offer is rewritten to use PT 100 the way the robot's is.
        /// </summary>
        [TestMethod]
        public async Task Phase4d_Desktop_AudioSentBeforeNegotiation_UsesThePeersPayloadType()
        {
            if (OperatingSystem.IsBrowser()) return;

            var config = new RTCPeerConnectionConfig();
            using var robot = RTCPeerConnectionFactory.Create(config);      // the offerer, like the robot
            using var client = RTCPeerConnectionFactory.Create(config);     // the answerer, streaming audio
            robot.OnIceCandidate += c => _ = client.AddIceCandidate(c);
            client.OnIceCandidate += c => _ = robot.AddIceCandidate(c);

            var desktopRobot = (DesktopRTCPeerConnection)robot;
            using var sink = new SineWaveAudioTrack(frequencyHz: 0.0, sampleRateHz: 48000, channels: 2);
            desktopRobot.AddTrack(sink);
            robot.CreateDataChannel("signal");

            var sender = client.AddPcmAudioTrack(48000, 2);

            var payloadTypes = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
            desktopRobot.NativeConnection.OnRtpPacketReceived += (endpoint, mediaType, packet) =>
            {
                if (mediaType == SIPSorcery.Net.SDPMediaTypesEnum.audio) payloadTypes.AddOrUpdate(packet.Header.PayloadType, 1, (_, n) => n + 1);
            };
            var decoded = 0;
            desktopRobot.NativeConnection.OnAudioFrameReceived += frame => { if (frame?.EncodedAudio is { Length: > 0 }) Interlocked.Increment(ref decoded); };

            // Sending starts now, before any SDP has been exchanged - 20 ms frames of a quiet tone, like a streamer holding the line open.
            using var stop = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                var frame = new short[960 * 2];
                for (var i = 0; i < 960; i++) { var v = (short)(Math.Sin(i * 0.1) * 3000); frame[2 * i] = v; frame[2 * i + 1] = v; }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long sent = 0;
                while (!stop.IsCancellationRequested)
                {
                    sender.PushPcm(new AudioPcmFrame(frame, 48000, 2));
                    sent++;
                    var wait = sent * 20 - clock.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay((int)wait);
                }
            });

            await Task.Delay(300);                                          // frames are already going out unnegotiated

            var offer = await robot.CreateOffer();
            var rewritten = RewriteOpusPayloadType(offer.Sdp!, 111, 100);
            if (rewritten == offer.Sdp) throw new Exception("test setup: the offer did not use payload type 111 to rewrite:\n" + offer.Sdp);
            offer = new RTCSessionDescriptionInit { Type = offer.Type, Sdp = rewritten };
            await robot.SetLocalDescription(offer);
            await client.SetRemoteDescription(offer);
            var answer = await client.CreateAnswer();
            await client.SetLocalDescription(answer);
            await robot.SetRemoteDescription(answer);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref decoded) < 5) await Task.Delay(50);
            stop.Cancel();
            await pump;

            var seen = string.Join(", ", payloadTypes.Select(kv => $"PT {kv.Key} x{kv.Value}"));
            if (!payloadTypes.ContainsKey(100))
                throw new Exception($"the answerer never sent with the negotiated payload type 100; the RTP it sent used: {(seen == "" ? "(nothing arrived)" : seen)}" + "\nOFFER:\n" + rewritten + "\nANSWER:\n" + answer.Sdp);
            if (payloadTypes.ContainsKey(111))
                throw new Exception($"the answerer sent with its own default payload type 111 as well as the negotiated one: {seen}");
            if (Volatile.Read(ref decoded) < 5) throw new Exception($"payload type was right ({seen}) but fewer than 5 audio frames reached the offerer ({decoded})");
        }

        // Moves the Opus payload type of an SDP: the m=audio format list and the a=rtpmap/fmtp/rtcp-fb lines for it.
        private static string RewriteOpusPayloadType(string sdp, int from, int to)
        {
            var lines = sdp.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var inAudio = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("m=")) inAudio = line.StartsWith("m=audio");
                if (!inAudio) continue;
                if (line.StartsWith("m=audio")) lines[i] = Regex.Replace(line, $@"(?<=\s){from}(?=\s|$)", to.ToString()).Replace(" 0 8 9 ", " ");      // Opus only, like the robot's offer
                else if (Regex.IsMatch(line, @"^a=rtpmap:(0|8|9) ")) lines[i] = null!;
                else lines[i] = Regex.Replace(line, $@"^(a=(?:rtpmap|fmtp|rtcp-fb):){from}(?=\s)", "${1}" + to);
            }
            return string.Join("\r\n", lines.Where(l => l != null));
        }
    }
}
