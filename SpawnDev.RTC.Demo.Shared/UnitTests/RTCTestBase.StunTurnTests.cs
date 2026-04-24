using SpawnDev.UnitTesting;
using System.Net;
using System.Net.Sockets;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    /// <summary>
    /// Tests for the embedded STUN/TURN server wired into
    /// <c>SpawnDev.RTC.Server.StunTurnServerHostedService</c>. These exercise the
    /// underlying SipSorcery <c>TurnServer</c> directly (which is what the hosted
    /// service wraps) - ensures STUN binding requests get a valid XOR-MAPPED-ADDRESS
    /// response, proving the server listens + responds correctly under the same
    /// configuration shape the hosted service uses.
    ///
    /// Desktop-only: browsers have no UDP sockets.
    /// </summary>
    public abstract partial class RTCTestBase
    {
        [TestMethod]
        public async Task StunServer_Loopback_BindingRequest_ReturnsXorMappedAddress()
        {
            if (OperatingSystem.IsBrowser()) return; // Browser has no UDP

            const int listenPort = 63478;

            // Configure exactly as StunTurnServerHostedService would for loopback defaults.
            var config = new SIPSorcery.Net.TurnServerConfig
            {
                ListenAddress = IPAddress.Loopback,
                Port = listenPort,
                EnableUdp = true,
                EnableTcp = false, // STUN binding is UDP; skip TCP bind for this test.
                RelayAddress = IPAddress.Loopback,
                Username = "test-user",
                Password = "test-pass",
                Realm = "spawndev-rtc-test",
            };

            using var server = new SIPSorcery.Net.TurnServer(config);
            server.Start();

            try
            {
                await Task.Delay(200); // let the listener bind

                // Send a STUN Binding Request via the library's STUNClient
                // (`SIPSorcery.Net.STUNClient.GetPublicIPEndPoint` is our fork's helper;
                // falls back to a manual send if it's not present on the loaded version).
                var publicEp = await SendBindingRequestAsync(IPAddress.Loopback, listenPort, TimeSpan.FromSeconds(3));
                if (publicEp == null)
                    throw new Exception("STUN binding response did not arrive within 3s");
                if (publicEp.Address == null)
                    throw new Exception("STUN binding response had no Address");
                if (publicEp.Port <= 0)
                    throw new Exception($"STUN binding response Port={publicEp.Port} (expected > 0)");

                // We don't assert equality with the sender's ephemeral port because
                // the test socket uses an ephemeral - just assert the response is
                // well-formed and the address resolves to loopback.
                if (!publicEp.Address.Equals(IPAddress.Loopback))
                    throw new Exception($"STUN response Address={publicEp.Address}, expected {IPAddress.Loopback}");
            }
            finally
            {
                server.Stop();
            }
        }

        /// <summary>
        /// Send a minimal STUN Binding Request (RFC 5389, no auth) and return the
        /// XOR-MAPPED-ADDRESS from the response, or null on timeout.
        /// </summary>
        private static async Task<IPEndPoint?> SendBindingRequestAsync(IPAddress host, int port, TimeSpan timeout)
        {
            using var udp = new UdpClient(0, AddressFamily.InterNetwork);
            var targetEp = new IPEndPoint(host, port);

            // STUN Binding Request: 20-byte header, no attributes.
            //   [0..1] message type = 0x0001 (Binding Request)
            //   [2..3] message length (excluding header) = 0
            //   [4..7] magic cookie = 0x2112A442
            //   [8..19] 12-byte transaction ID (random)
            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[2] = 0x00; req[3] = 0x00;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            System.Security.Cryptography.RandomNumberGenerator.Fill(req.AsSpan(8, 12));

            await udp.SendAsync(req, req.Length, targetEp);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                return ParseXorMappedAddress(result.Buffer);
            }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>
        /// Minimal STUN response parser — finds the XOR-MAPPED-ADDRESS attribute
        /// (type 0x0020) and returns its IP + port after un-XORing with the magic
        /// cookie. Good enough for a round-trip test; not a full STUN decoder.
        /// </summary>
        private static IPEndPoint? ParseXorMappedAddress(byte[] resp)
        {
            if (resp.Length < 20) return null;
            // Header type must be Binding Success Response = 0x0101
            if (resp[0] != 0x01 || resp[1] != 0x01) return null;

            int attrStart = 20;
            int msgLen = (resp[2] << 8) | resp[3];
            int end = Math.Min(attrStart + msgLen, resp.Length);
            int i = attrStart;
            while (i + 4 <= end)
            {
                int attrType = (resp[i] << 8) | resp[i + 1];
                int attrLen = (resp[i + 2] << 8) | resp[i + 3];
                int dataStart = i + 4;
                if (dataStart + attrLen > end) break;

                if (attrType == 0x0020 && attrLen >= 8) // XOR-MAPPED-ADDRESS, IPv4
                {
                    // Family (1 byte skip + 1 byte family), port (2 bytes), address (4 bytes)
                    int xorPort = ((resp[dataStart + 2] << 8) | resp[dataStart + 3]) ^ 0x2112;
                    int a0 = resp[dataStart + 4] ^ 0x21;
                    int a1 = resp[dataStart + 5] ^ 0x12;
                    int a2 = resp[dataStart + 6] ^ 0xA4;
                    int a3 = resp[dataStart + 7] ^ 0x42;
                    return new IPEndPoint(new IPAddress(new[] { (byte)a0, (byte)a1, (byte)a2, (byte)a3 }), xorPort);
                }

                // Pad attrLen to 4-byte boundary per RFC 5389 §15.
                i = dataStart + ((attrLen + 3) & ~3);
            }
            return null;
        }
    }
}
