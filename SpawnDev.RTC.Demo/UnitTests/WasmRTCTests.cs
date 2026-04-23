using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.RTC.Demo.Shared.UnitTests;
using SpawnDev.UnitTesting;
using System.Text.Json;

namespace SpawnDev.RTC.Demo.UnitTests
{
    public class WasmRTCTests : RTCTestBase
    {
        public WasmRTCTests() : base()
        {

        }

        /// <summary>
        /// Diagnostic: does RtcPeerConnectionRoomHandler.CreateOffersAsync actually work on
        /// a Blazor WASM browser context? The library-level SignalingEmbeddedTest runs on
        /// desktop SipSorcery which has different type-loading behavior. This test proves
        /// whether the browser can JIT the factory-call path without tripping over the
        /// Desktop-branch SIPSorcery type metadata.
        /// </summary>
        [TestMethod(Timeout = 15_000)]
        public async Task Signaling_CreateOffers_WorksOnBrowser()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };
            using var handler = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            var offers = await handler.CreateOffersAsync(1, CancellationToken.None);
            if (offers == null || offers.Count == 0) throw new Exception("Handler returned no offers");
            var offer = offers[0];
            if (offer.OfferId is null || offer.OfferId.Length != 20) throw new Exception($"OfferId length {offer.OfferId?.Length ?? -1}, expected 20");
            if (string.IsNullOrEmpty(offer.OfferSdp)) throw new Exception("Offer Sdp is empty");
            if (!offer.OfferSdp.Contains("webrtc-datachannel")) throw new Exception("Offer Sdp missing data channel section");
        }

        /// <summary>
        /// Diagnostic: verify that two Browser RTCPeerConnections can complete ICE/DTLS and
        /// open a data channel inside this Playwright Chromium context. If this passes but the
        /// ChatDemo test doesn't, the bottleneck is somewhere in the tracker/signaling/popup
        /// layer rather than raw WebRTC.
        /// </summary>
        [TestMethod(Timeout = 15_000)]
        public async Task Signaling_BrowserLoopback_DataChannelOpens()
        {
            var config = new RTCPeerConnectionConfig();
            using var pcA = RTCPeerConnectionFactory.Create(config);
            using var pcB = RTCPeerConnectionFactory.Create(config);
            using var dcA = pcA.CreateDataChannel("diag");

            pcA.OnIceCandidate += c => { if (c != null) pcB.AddIceCandidate(c); };
            pcB.OnIceCandidate += c => { if (c != null) pcA.AddIceCandidate(c); };

            var opened = new TaskCompletionSource<bool>();
            IRTCDataChannel? dcB = null;
            pcB.OnDataChannel += ch => { dcB = ch; ch.OnOpen += () => opened.TrySetResult(true); };
            dcA.OnOpen += () => opened.TrySetResult(true);

            var offer = await pcA.CreateOffer();
            await pcA.SetLocalDescription(offer);
            await pcB.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = offer.Sdp });
            var answer = await pcB.CreateAnswer();
            await pcB.SetLocalDescription(answer);
            await pcA.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answer.Sdp });

            var winner = await Task.WhenAny(opened.Task, Task.Delay(10_000));
            if (winner != opened.Task) throw new Exception("data channel never opened (10s)");
        }

        /// <summary>
        /// Diagnostic: isolate whether the SIPSorcery load error is triggered by
        /// RTCPeerConnectionFactory.Create itself, or by something the handler does around it.
        /// The existing PeerConnection_CanCreate test calls Create() directly and passes
        /// on browser, so this SHOULD also pass. If it fails, Create() is the culprit.
        /// </summary>
        [TestMethod(Timeout = 15_000)]
        public async Task Signaling_FactoryCreate_WithConfig_WorksOnBrowser()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };
            using var pc = RTCPeerConnectionFactory.Create(config);
            using var dc = pc.CreateDataChannel("diag");
            var offer = await pc.CreateOffer();
            if (string.IsNullOrEmpty(offer.Sdp)) throw new Exception("Offer Sdp empty");
        }

        /// <summary>
        /// End-to-end test of the ChatRoom.razor demo page. Spawns two iframes pointed at
        /// <c>/chat</c> with opposite auto-play query params (one auto-sends, one auto-replies)
        /// against the live openwebtorrent tracker, then waits for the round-trip to complete.
        ///
        /// <para>Purpose: cover the component-level wiring (InvokeAsync ordering, _peers dict,
        /// peer.ChatChannel.Send) that library-level signaling tests miss because they run in
        /// desktop SipSorcery with no Blazor dispatcher.</para>
        /// </summary>
        [TestMethod(Timeout = 120_000)]
        public async Task ChatDemo_TextChat_RoundTrips_BetweenTwoIframes()
        {
            var JS = BlazorJSRuntime.JS;

            var room = "testroom-" + Guid.NewGuid().ToString("N")[..8];
            // Use the test server's embedded tracker (mounted via UseRtcSignaling) instead of
            // the live openwebtorrent.com tracker - faster, deterministic, no internet dep.
            var origin = JS.Get<string>("window.location.origin");
            var tracker = origin.Replace("https://", "wss://").Replace("http://", "ws://") + "/announce";
            var idA = "A" + Guid.NewGuid().ToString("N")[..6];
            var idB = "B" + Guid.NewGuid().ToString("N")[..6];
            var pingFromA = $"PING-FROM-{idA}";
            var pongFromB = $"PONG-FROM-{idB}";

            // Resolve /chat against document.baseURI so it works under GitHub Pages subpaths too
            var baseUri = JS.Get<string>("document.baseURI");
            string BuildUrl(string id, string autoSend, string autoReply) =>
                baseUri.TrimEnd('/') + "/chat?"
                    + $"room={Uri.EscapeDataString(room)}"
                    + $"&tracker={Uri.EscapeDataString(tracker)}"
                    + $"&testId={Uri.EscapeDataString(id)}"
                    + $"&noMedia=true&autoJoin=true"
                    + (string.IsNullOrEmpty(autoSend) ? "" : $"&autoSend={Uri.EscapeDataString(autoSend)}")
                    + (string.IsNullOrEmpty(autoReply) ? "" : $"&autoReply={Uri.EscapeDataString(autoReply)}");

            var urlA = BuildUrl(idA, autoSend: pingFromA, autoReply: "");
            var urlB = BuildUrl(idB, autoSend: "", autoReply: pongFromB);

            // Clear any prior test state / diagnostic slots
            string[] slots =
            {
                $"chatRoomState_{idA}", $"chatRoomState_{idB}",
                $"chatRoomAlive_{idA}", $"chatRoomAlive_{idB}",
                $"chatRoomJoinStart_{idA}", $"chatRoomJoinStart_{idB}",
                $"chatRoomTrackerConnected_{idA}", $"chatRoomTrackerConnected_{idB}",
                $"chatRoomAnnounced_{idA}", $"chatRoomAnnounced_{idB}",
                $"chatRoomError_{idA}", $"chatRoomError_{idB}",
                $"chatRoomTrackerWarning_{idA}", $"chatRoomTrackerWarning_{idB}",
            };
            foreach (var k in slots) JS.Set(k, (object?)null);

            // Use window.open popups instead of iframes. Top-level browsing contexts load
            // Blazor WASM the same way the parent /tests page does - in testing iframes
            // hit mysterious resource 404s on SIPSorcery type metadata that popups don't.
            using var winA = JS.Call<Window>("window.open", urlA, "_blank", "width=320,height=240");
            using var winB = JS.Call<Window>("window.open", urlB, "_blank", "width=320,height=240");
            if (winA == null || winB == null)
                throw new Exception("window.open returned null - popup blocked?");

            try
            {
                string Ph(string id)
                {
                    var alive = JS.Get<long?>($"chatRoomAlive_{id}") != null ? "Init" : "-";
                    var join = JS.Get<long?>($"chatRoomJoinStart_{id}") != null ? "Join" : "-";
                    var trCon = JS.Get<long?>($"chatRoomTrackerConnected_{id}") != null ? "TrCon" : "-";
                    var ann = JS.Get<long?>($"chatRoomAnnounced_{id}") != null ? "Ann" : "-";
                    var err = JS.Get<string?>($"chatRoomError_{id}");
                    var warn = JS.Get<string?>($"chatRoomTrackerWarning_{id}");
                    var e = err != null ? $" ERR:{err}" : "";
                    var w = !string.IsNullOrEmpty(warn) ? $" WARN:{warn}" : "";
                    return $"{alive}/{join}/{trCon}/{ann}{e}{w}";
                }
                string Diag()
                {
                    var a = GetState(JS, idA);
                    var b = GetState(JS, idB);
                    return $"A[{Ph(idA)}] {{j={a?.Joined},p={a?.PeerCount},o={a?.OpenChannels},in={a?.LastIncoming ?? "(null)"}}} | " +
                           $"B[{Ph(idB)}] {{j={b?.Joined},p={b?.PeerCount},o={b?.OpenChannels},in={b?.LastIncoming ?? "(null)"}}}";
                }

                await WaitFor(
                    () => GetState(JS, idA)?.OpenChannels >= 1 && GetState(JS, idB)?.OpenChannels >= 1,
                    timeoutSeconds: 60,
                    label: "both windows have at least one open data channel",
                    diagDump: Diag);

                await WaitFor(
                    () => GetState(JS, idA)?.LastIncoming == pongFromB,
                    timeoutSeconds: 20,
                    label: $"iframe A received '{pongFromB}' (auto-reply from B)",
                    diagDump: Diag);

                await WaitFor(
                    () => GetState(JS, idB)?.LastIncoming == pingFromA,
                    timeoutSeconds: 5,
                    label: $"iframe B received '{pingFromA}' (auto-send from A)",
                    diagDump: Diag);
            }
            finally
            {
                try { winA.Close(); } catch { }
                try { winB.Close(); } catch { }
                foreach (var k in slots) JS.Set(k, (object?)null);
            }
        }

        private record ChatState(bool Joined, int PeerCount, int OpenChannels, string? LastIncoming, int IncomingCount);

        private static ChatState? GetState(BlazorJSRuntime js, string id)
        {
            try
            {
                var je = js.Get<JsonElement?>($"chatRoomState_{id}");
                if (je is null || je.Value.ValueKind != JsonValueKind.Object) return null;
                var el = je.Value;
                return new ChatState(
                    Joined: el.TryGetProperty("joined", out var j) && j.GetBoolean(),
                    PeerCount: el.TryGetProperty("peerCount", out var pc) ? pc.GetInt32() : 0,
                    OpenChannels: el.TryGetProperty("openChannels", out var oc) ? oc.GetInt32() : 0,
                    LastIncoming: el.TryGetProperty("lastIncoming", out var li) && li.ValueKind == JsonValueKind.String ? li.GetString() : null,
                    IncomingCount: el.TryGetProperty("incomingCount", out var ic) ? ic.GetInt32() : 0);
            }
            catch { return null; }
        }

        private static async Task WaitFor(Func<bool> predicate, int timeoutSeconds, string label, Func<string>? diagDump = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var nextDiag = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                try { if (predicate()) return; } catch { }
                if (diagDump != null && DateTime.UtcNow >= nextDiag)
                {
                    try { Console.WriteLine($"[WaitFor] {label}: {diagDump()}"); } catch { }
                    nextDiag = DateTime.UtcNow.AddSeconds(3);
                }
                await Task.Delay(200);
            }
            var final = diagDump != null ? $" (final state: {diagDump()})" : "";
            throw new Exception($"Timeout ({timeoutSeconds}s) waiting for: {label}{final}");
        }
    }
}
