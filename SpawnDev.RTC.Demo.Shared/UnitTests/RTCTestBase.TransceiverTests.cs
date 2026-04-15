using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify AddTransceiver creates an audio transceiver.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_AddAudio()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("audio");
            if (transceiver == null) throw new Exception("AddTransceiver returned null");
            if (transceiver.Sender == null) throw new Exception("Transceiver.Sender is null");
            if (transceiver.Receiver == null) throw new Exception("Transceiver.Receiver is null");
            if (transceiver.Direction != "sendrecv") throw new Exception($"Direction should be 'sendrecv', got '{transceiver.Direction}'");

            var transceivers = pc.GetTransceivers();
            if (transceivers.Length == 0) throw new Exception("GetTransceivers returned empty after AddTransceiver");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify AddTransceiver creates a video transceiver.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_AddVideo()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("video");
            if (transceiver == null) throw new Exception("AddTransceiver returned null");
            if (transceiver.Direction != "sendrecv") throw new Exception($"Direction: '{transceiver.Direction}'");

            // Create offer should include video media line
            var offer = await pc.CreateOffer();
            if (!offer.Sdp.Contains("m=video")) throw new Exception("Offer SDP missing video media line");
        }

        /// <summary>
        /// Verify transceiver direction can be changed.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_DirectionChange()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("audio");

            transceiver.Direction = "sendonly";
            if (transceiver.Direction != "sendonly") throw new Exception($"Direction should be 'sendonly', got '{transceiver.Direction}'");

            transceiver.Direction = "recvonly";
            if (transceiver.Direction != "recvonly") throw new Exception($"Direction should be 'recvonly', got '{transceiver.Direction}'");

            transceiver.Direction = "inactive";
            if (transceiver.Direction != "inactive") throw new Exception($"Direction should be 'inactive', got '{transceiver.Direction}'");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify transceiver Stop() sets direction to stopped.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_Stop()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("audio");

            transceiver.Stop();
            if (transceiver.Direction != "stopped") throw new Exception($"Direction after stop: '{transceiver.Direction}'");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify multiple transceivers can be added.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_Multiple()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            pc.AddTransceiver("audio");
            pc.AddTransceiver("video");

            var transceivers = pc.GetTransceivers();
            if (transceivers.Length < 2) throw new Exception($"Expected at least 2 transceivers, got {transceivers.Length}");

            // Offer should contain both audio and video
            var offer = await pc.CreateOffer();
            if (!offer.Sdp.Contains("m=audio")) throw new Exception("Offer missing audio");
            if (!offer.Sdp.Contains("m=video")) throw new Exception("Offer missing video");
        }
    }
}
