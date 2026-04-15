using SpawnDev.RTC;
using SpawnDev.UnitTesting;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Connect to the signal server, exchange SDP with a remote peer,
        /// and send a data channel message. This tests real cross-platform
        /// connectivity (desktop to browser or browser to browser via signal server).
        ///
        /// Requires SpawnDev.RTC.SignalServer running on localhost:5571.
        /// </summary>
        [TestMethod]
        public async Task Signal_DataChannel_CrossPlatform()
        {
            // Use HTTP for WASM (avoids self-signed cert issues), HTTPS for desktop
            var signalUrl = OperatingSystem.IsBrowser()
                ? "ws://localhost:5572/signal/rtc-test"
                : "wss://localhost:5571/signal/rtc-test";
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc = RTCPeerConnectionFactory.Create(config);

            // Connect to signal server
            using var ws = new ClientWebSocket();
            // Skip cert validation on desktop (self-signed dev cert)
            // Browser WASM doesn't support this property
            if (!OperatingSystem.IsBrowser())
            {
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }
            try
            {
                await ws.ConnectAsync(new Uri(signalUrl), CancellationToken.None);
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not connect to signal server at {signalUrl}: {ex.Message}. Start SpawnDev.RTC.SignalServer first.");
            }

            var myPeerId = "";
            var remotePeerId = "";
            var isInitiator = false;

            // Data channel for message exchange
            IRTCDataChannel? dc = null;
            var messageReceived = new TaskCompletionSource<string>();
            var channelOpened = new TaskCompletionSource<bool>();

            // Handle incoming data channel (we're the responder)
            pc.OnDataChannel += channel =>
            {
                dc = channel;
                dc.OnOpen += () => channelOpened.TrySetResult(true);
                dc.OnStringMessage += msg => messageReceived.TrySetResult(msg);
            };

            // ICE candidates -> signal server
            pc.OnIceCandidate += async candidate =>
            {
                await WsSendAsync(ws, new
                {
                    type = "ice-candidate",
                    targetId = remotePeerId,
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex,
                });
            };

            // Process signal server messages
            var signalTask = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = JsonSerializer.Deserialize<JsonElement>(json);
                    var msgType = msg.GetProperty("type").GetString();

                    switch (msgType)
                    {
                        case "welcome":
                            myPeerId = msg.GetProperty("peerId").GetString()!;
                            var peers = msg.GetProperty("peers");
                            if (peers.GetArrayLength() > 0)
                            {
                                // There's already a peer in the room - we initiate
                                isInitiator = true;
                                remotePeerId = peers[0].GetString()!;

                                dc = pc.CreateDataChannel("cross-platform-test");
                                dc.OnOpen += () => channelOpened.TrySetResult(true);
                                dc.OnStringMessage += m => messageReceived.TrySetResult(m);

                                var offer = await pc.CreateOffer();
                                await pc.SetLocalDescription(offer);
                                await WsSendAsync(ws, new
                                {
                                    type = "offer",
                                    targetId = remotePeerId,
                                    sdp = offer.Sdp,
                                });
                            }
                            break;

                        case "peer-joined":
                            // New peer arrived - they will initiate, we wait
                            remotePeerId = msg.GetProperty("peerId").GetString()!;
                            break;

                        case "offer":
                            remotePeerId = msg.GetProperty("fromId").GetString()!;
                            var offerSdp = msg.GetProperty("sdp").GetString()!;
                            await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = offerSdp });
                            var answer = await pc.CreateAnswer();
                            await pc.SetLocalDescription(answer);
                            await WsSendAsync(ws, new
                            {
                                type = "answer",
                                targetId = remotePeerId,
                                sdp = answer.Sdp,
                            });
                            break;

                        case "answer":
                            var answerSdp = msg.GetProperty("sdp").GetString()!;
                            await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answerSdp });
                            break;

                        case "ice-candidate":
                            var candidateStr = msg.GetProperty("candidate").GetString()!;
                            string? sdpMid = msg.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null;
                            int? sdpMLineIndex = msg.TryGetProperty("sdpMLineIndex", out var mli) ? mli.GetInt32() : null;
                            await pc.AddIceCandidate(new RTCIceCandidateInit
                            {
                                Candidate = candidateStr,
                                SdpMid = sdpMid,
                                SdpMLineIndex = sdpMLineIndex,
                            });
                            break;
                    }
                }
            });

            // Wait for channel to open (30s timeout for cross-platform DTLS + ICE)
            var openResult = await Task.WhenAny(channelOpened.Task, Task.Delay(30000));
            if (openResult != channelOpened.Task)
                throw new Exception($"Data channel did not open in 30s. Initiator: {isInitiator}, Remote: {remotePeerId}");

            // Send a test message
            var platform = OperatingSystem.IsBrowser() ? "browser" : "desktop";
            dc!.Send($"Hello from {platform} ({myPeerId})!");

            // Wait for response
            var msgResult = await Task.WhenAny(messageReceived.Task, Task.Delay(10000));
            if (msgResult != messageReceived.Task)
                throw new Exception("Did not receive response message in 10s");

            var received = await messageReceived.Task;
            if (string.IsNullOrEmpty(received))
                throw new Exception("Received empty message");

            // Success!
            Console.WriteLine($"Cross-platform message exchange: sent from {platform}, received: '{received}'");
        }

        private static async Task WsSendAsync(ClientWebSocket ws, object message)
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
