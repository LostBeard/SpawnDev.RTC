using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Two peers connect via RTCTrackerClient on the embedded test tracker
        /// and exchange a data channel message.
        /// </summary>
        [TestMethod]
        public async Task Tracker_Embedded_TwoPeers_DataChannel()
        {
            // Embedded tracker runs on the PlaywrightMultiTest server at /announce
            // Desktop: wss://localhost:5570/announce, Browser: same origin
            var trackerUrl = OperatingSystem.IsBrowser()
                ? "wss://localhost:5570/announce"
                : "wss://localhost:5570/announce";

            var roomName = "tracker-test-" + Guid.NewGuid().ToString("N")[..6];
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            // Peer A
            using var trackerA = new RTCTrackerClient(trackerUrl, roomName, config);
            var dcAReceived = new TaskCompletionSource<string>();

            trackerA.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                pc.CreateDataChannel("test");
                await Task.CompletedTask;
            };

            trackerA.OnDataChannel += (channel, peerId) =>
            {
                channel.OnStringMessage += msg => dcAReceived.TrySetResult(msg);
                channel.OnOpen += () => channel.Send("hello from A");
            };

            // Peer B
            using var trackerB = new RTCTrackerClient(trackerUrl, roomName, config);
            var dcBReceived = new TaskCompletionSource<string>();

            trackerB.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                pc.CreateDataChannel("test");
                await Task.CompletedTask;
            };

            trackerB.OnDataChannel += (channel, peerId) =>
            {
                channel.OnStringMessage += msg => dcBReceived.TrySetResult(msg);
                channel.OnOpen += () => channel.Send("hello from B");
            };

            // Join - A first, then B after a moment
            await trackerA.JoinAsync();
            await Task.Delay(500);
            await trackerB.JoinAsync();

            // Wait for data channel messages
            var resultA = await Task.WhenAny(dcAReceived.Task, Task.Delay(30000));
            var resultB = await Task.WhenAny(dcBReceived.Task, Task.Delay(30000));

            if (resultA != dcAReceived.Task) throw new Exception("Peer A did not receive message from B");
            if (resultB != dcBReceived.Task) throw new Exception("Peer B did not receive message from A");

            var msgA = await dcAReceived.Task;
            var msgB = await dcBReceived.Task;

            if (msgA != "hello from B") throw new Exception($"A received: '{msgA}'");
            if (msgB != "hello from A") throw new Exception($"B received: '{msgB}'");
        }

        /// <summary>
        /// Two desktop peers connect via the live openwebtorrent tracker.
        /// This test verifies the tracker protocol works with real infrastructure.
        /// </summary>
        [TestMethod]
        public async Task Tracker_Live_OpenWebTorrent_TwoPeers()
        {
            // Only run on desktop - browser can't create two tracker clients easily
            // and we don't want to spam the public tracker from browser tests
            if (OperatingSystem.IsBrowser())
            {
                // Skip on browser - desktop-only integration test
                return;
            }

            var trackerUrl = "wss://tracker.openwebtorrent.com";
            var roomName = "spawndev-rtc-test-" + Guid.NewGuid().ToString("N")[..8];
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var trackerA = new RTCTrackerClient(trackerUrl, roomName, config);
            var messageFromB = new TaskCompletionSource<string>();

            trackerA.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                pc.CreateDataChannel("live-test");
                await Task.CompletedTask;
            };

            trackerA.OnDataChannel += (channel, peerId) =>
            {
                channel.OnStringMessage += msg => messageFromB.TrySetResult(msg);
                channel.OnOpen += () => channel.Send("hello from desktop A");
            };

            using var trackerB = new RTCTrackerClient(trackerUrl, roomName, config);
            var messageFromA = new TaskCompletionSource<string>();

            trackerB.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                pc.CreateDataChannel("live-test");
                await Task.CompletedTask;
            };

            trackerB.OnDataChannel += (channel, peerId) =>
            {
                channel.OnStringMessage += msg => messageFromA.TrySetResult(msg);
                channel.OnOpen += () => channel.Send("hello from desktop B");
            };

            await trackerA.JoinAsync();
            await Task.Delay(1000); // Give tracker time to process A's announce
            await trackerB.JoinAsync();

            // Wait for messages (longer timeout for live tracker + ICE)
            var resultA = await Task.WhenAny(messageFromB.Task, Task.Delay(45000));
            var resultB = await Task.WhenAny(messageFromA.Task, Task.Delay(45000));

            if (resultA != messageFromB.Task) throw new Exception("Desktop A did not receive message from B via live tracker");
            if (resultB != messageFromA.Task) throw new Exception("Desktop B did not receive message from A via live tracker");

            var msgA = await messageFromB.Task;
            var msgB = await messageFromA.Task;

            if (!msgA.Contains("hello from desktop B")) throw new Exception($"A got: '{msgA}'");
            if (!msgB.Contains("hello from desktop A")) throw new Exception($"B got: '{msgB}'");
        }
    }
}
