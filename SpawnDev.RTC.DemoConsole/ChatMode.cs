using SpawnDev.RTC;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.RTC.DemoConsole
{
    /// <summary>
    /// Desktop text chat using SpawnDev.RTC.
    /// Joins the same swarms as the browser ChatRoom demo.
    /// Room name is hashed to an infohash for swarm-style signaling.
    /// </summary>
    public static class ChatMode
    {
        public static async Task Run(string signalServerBase = "wss://localhost:5570")
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

            var infoHash = ComputeInfoHash(roomName);
            Console.WriteLine($"Room: {roomName}");
            Console.WriteLine($"Swarm ID: {infoHash}");
            Console.WriteLine();

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var signalUrl = $"{signalServerBase}/signal/{infoHash}";
            using var signal = new RTCSignalClient(signalUrl, config);

            signal.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                // Create data channel for text chat
                var dc = pc.CreateDataChannel("chat");
                dc.OnOpen += () => Console.WriteLine($"[Connected to {peerId[..6]}]");
                dc.OnStringMessage += msg => Console.WriteLine($"  {peerId[..6]}: {msg}");
                await Task.CompletedTask;
            };

            signal.OnDataChannel += (channel, peerId) =>
            {
                channel.OnStringMessage += msg => Console.WriteLine($"  {peerId[..6]}: {msg}");
            };

            signal.OnPeerDisconnected += peerId =>
            {
                Console.WriteLine($"[{peerId[..6]} disconnected]");
            };

            signal.OnConnected += () => Console.WriteLine("[Connected to signal server]");
            signal.OnDisconnected += () => Console.WriteLine("[Disconnected from signal server]");

            try
            {
                await signal.ConnectAsync();
                Console.WriteLine("[Waiting for peers... Type messages and press Enter to send]");
                Console.WriteLine("[Type 'quit' to leave]");
                Console.WriteLine();

                // Read input loop
                while (true)
                {
                    var input = Console.ReadLine();
                    if (input == null || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    // Send to all peers via their data channels
                    // (RTCSignalClient manages the peer connections internally,
                    //  but we need access to the data channels we created)
                    Console.WriteLine($"  You: {input}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("[Left room]");
        }

        private static string ComputeInfoHash(string roomName)
        {
            var bytes = Encoding.UTF8.GetBytes(roomName.Trim().ToLowerInvariant());
            var hash = SHA1.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
