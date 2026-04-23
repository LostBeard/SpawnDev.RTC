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
        /// End-to-end test of the ChatRoom.razor demo page. Spawns two iframes pointed at
        /// <c>/chat</c> with opposite auto-play query params (one auto-sends, one auto-replies)
        /// against the live openwebtorrent tracker, then waits for the round-trip to complete.
        ///
        /// <para>Purpose: cover the component-level wiring (InvokeAsync ordering, _peers dict,
        /// peer.ChatChannel.Send) that library-level signaling tests miss because they run in
        /// desktop SipSorcery with no Blazor dispatcher.</para>
        /// </summary>
        [TestMethod]
        public async Task ChatDemo_TextChat_RoundTrips_BetweenTwoIframes()
        {
            var JS = BlazorJSRuntime.JS;

            var room = "testroom-" + Guid.NewGuid().ToString("N")[..8];
            var tracker = "wss://tracker.openwebtorrent.com";
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
                    + $"&noMedia=1&autoJoin=1"
                    + (string.IsNullOrEmpty(autoSend) ? "" : $"&autoSend={Uri.EscapeDataString(autoSend)}")
                    + (string.IsNullOrEmpty(autoReply) ? "" : $"&autoReply={Uri.EscapeDataString(autoReply)}");

            var urlA = BuildUrl(idA, autoSend: pingFromA, autoReply: "");
            var urlB = BuildUrl(idB, autoSend: "", autoReply: pongFromB);

            // Clear any prior test state slots
            JS.Set($"chatRoomState_{idA}", (object?)null);
            JS.Set($"chatRoomState_{idB}", (object?)null);

            using var doc = JS.Get<Document>("document");
            using var body = doc.Body ?? throw new Exception("document.body missing");

            using var iframeA = doc.CreateElement<HTMLIFrameElement>("iframe");
            using var iframeB = doc.CreateElement<HTMLIFrameElement>("iframe");
            iframeA.Width = "320"; iframeA.Height = "240"; iframeA.Src = urlA;
            iframeB.Width = "320"; iframeB.Height = "240"; iframeB.Src = urlB;
            body.AppendChild(iframeA);
            body.AppendChild(iframeB);

            try
            {
                await WaitFor(
                    () => GetState(JS, idA)?.OpenChannels >= 1 && GetState(JS, idB)?.OpenChannels >= 1,
                    timeoutSeconds: 30,
                    label: "both iframes have at least one open data channel");

                await WaitFor(
                    () => GetState(JS, idA)?.LastIncoming == pongFromB,
                    timeoutSeconds: 20,
                    label: $"iframe A received '{pongFromB}' (auto-reply from B)");

                await WaitFor(
                    () => GetState(JS, idB)?.LastIncoming == pingFromA,
                    timeoutSeconds: 5,
                    label: $"iframe B received '{pingFromA}' (auto-send from A)");
            }
            finally
            {
                body.RemoveChild(iframeA);
                body.RemoveChild(iframeB);
                JS.Set($"chatRoomState_{idA}", (object?)null);
                JS.Set($"chatRoomState_{idB}", (object?)null);
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

        private static async Task WaitFor(Func<bool> predicate, int timeoutSeconds, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try { if (predicate()) return; } catch { }
                await Task.Delay(200);
            }
            throw new Exception($"Timeout ({timeoutSeconds}s) waiting for: {label}");
        }
    }
}
