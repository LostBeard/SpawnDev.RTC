using SpawnDev.RTC;
using SpawnDev.RTC.Signaling;
using System.Security.Cryptography;

namespace SpawnDev.RTC.DemoConsole
{
    /// <summary>
    /// Desktop text chat using SpawnDev.RTC.
    /// Joins the same swarms as the browser ChatRoom demo.
    /// Room name is hashed to a <see cref="RoomKey"/> for swarm-style signaling.
    /// </summary>
    public static class ChatMode
    {
        public static async Task Run(string trackerUrl = "wss://tracker.openwebtorrent.com")
        {
            Console.WriteLine("SpawnDev.RTC Desktop Chat");
            Console.WriteLine("========================");
            Console.WriteLine();
            Console.Write("Enter room name: ");
            var roomName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(roomName))
            {
                Console.WriteLine("No room name entered.");
                return;
            }

            var room = RoomKey.FromString(roomName);
            Console.WriteLine($"Room: {roomName}");
            Console.WriteLine($"Swarm ID: {room.ToHex()}");
            Console.WriteLine();

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var handler = new RtcPeerConnectionRoomHandler(config)
            {
                DataChannelLabel = "chat",
            };

            handler.OnDataChannel += (channel, peerId) =>
            {
                channel.OnOpen += () => Console.WriteLine($"[Connected to {peerId[..6]}]");
                channel.OnStringMessage += msg => Console.WriteLine($"  {peerId[..6]}: {msg}");
            };

            handler.OnPeerDisconnected += peerId => Console.WriteLine($"[{peerId[..6]} disconnected]");

            var peerId = new byte[20];
            RandomNumberGenerator.Fill(peerId);
            await using var client = new TrackerSignalingClient(trackerUrl, peerId);

            client.OnConnected += () => Console.WriteLine("[Connected to signal server]");
            client.OnDisconnected += () => Console.WriteLine("[Disconnected from signal server]");
            client.OnWarning += w => Console.WriteLine($"[Warning] {w}");

            client.Subscribe(room, handler);

            try
            {
                await client.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 5 });
                Console.WriteLine("[Waiting for peers... Type messages and press Enter to send]");
                Console.WriteLine("[Type 'quit' to leave]");
                Console.WriteLine();

                while (true)
                {
                    var input = Console.ReadLine();
                    if (input == null || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    Console.WriteLine($"  You: {input}");
                }

                await client.AnnounceAsync(room, new AnnounceOptions { Event = "stopped", NumWant = 0 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            client.Unsubscribe(room);
            Console.WriteLine("[Left room]");
        }
    }
}
