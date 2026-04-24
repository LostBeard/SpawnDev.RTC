using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    /// <summary>
    /// Tests for <see cref="SpawnDev.RTC.PerfectNegotiator"/> - the W3C glare-free
    /// renegotiation helper. Covers construction + disposal + offer collision
    /// resolution. Full end-to-end renegotiation-on-live-connection is covered by
    /// the existing dedicated <c>Renegotiation_*</c> tests; these tests focus on the
    /// helper's state-machine contract in isolation.
    /// </summary>
    public abstract partial class RTCTestBase
    {
        [TestMethod]
        public async Task PerfectNegotiator_CanConstruct_Polite()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var neg = new PerfectNegotiator(
                pc,
                polite: true,
                sendDescription: _ => Task.CompletedTask,
                sendCandidate: _ => Task.CompletedTask);

            if (!neg.Polite) throw new Exception("Polite flag should be true");
            if (neg.HasNegotiated) throw new Exception("HasNegotiated should start false");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_CanConstruct_Impolite()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var neg = new PerfectNegotiator(
                pc,
                polite: false,
                sendDescription: _ => Task.CompletedTask,
                sendCandidate: _ => Task.CompletedTask);

            if (neg.Polite) throw new Exception("Polite flag should be false");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_Ctor_NullPc_Throws()
        {
            try
            {
                using var neg = new PerfectNegotiator(
                    null!, polite: true,
                    _ => Task.CompletedTask, _ => Task.CompletedTask);
                throw new Exception("Expected ArgumentNullException for null pc");
            }
            catch (ArgumentNullException) { /* expected */ }
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_Ctor_NullSendDescription_Throws()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            try
            {
                using var neg = new PerfectNegotiator(
                    pc, polite: true,
                    sendDescription: null!,
                    sendCandidate: _ => Task.CompletedTask);
                throw new Exception("Expected ArgumentNullException for null sendDescription");
            }
            catch (ArgumentNullException) { /* expected */ }
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_Ctor_NullSendCandidate_Throws()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            try
            {
                using var neg = new PerfectNegotiator(
                    pc, polite: true,
                    sendDescription: _ => Task.CompletedTask,
                    sendCandidate: null!);
                throw new Exception("Expected ArgumentNullException for null sendCandidate");
            }
            catch (ArgumentNullException) { /* expected */ }
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_DisposeIsIdempotent()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var neg = new PerfectNegotiator(
                pc, polite: true,
                _ => Task.CompletedTask, _ => Task.CompletedTask);
            neg.Dispose();
            neg.Dispose(); // must not throw
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task PerfectNegotiator_HandleRemoteDescription_Null_Throws()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var neg = new PerfectNegotiator(
                pc, polite: true,
                _ => Task.CompletedTask, _ => Task.CompletedTask);

            try
            {
                await neg.HandleRemoteDescriptionAsync(null!);
                throw new Exception("Expected ArgumentNullException for null description");
            }
            catch (ArgumentNullException) { /* expected */ }
        }

        [TestMethod]
        public async Task PerfectNegotiator_HandleRemoteCandidate_Null_Throws()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var neg = new PerfectNegotiator(
                pc, polite: true,
                _ => Task.CompletedTask, _ => Task.CompletedTask);

            try
            {
                await neg.HandleRemoteCandidateAsync(null!);
                throw new Exception("Expected ArgumentNullException for null candidate");
            }
            catch (ArgumentNullException) { /* expected */ }
        }

        [TestMethod]
        public async Task PerfectNegotiator_AutoSendsOfferOnNegotiationNeeded()
        {
            // Creating a data channel on a fresh PC fires needsNegotiation, which the
            // helper should catch + call SetLocalDescription + ship the description via
            // the provided callback. We capture it in a TaskCompletionSource.
            using var pc = RTCPeerConnectionFactory.Create();
            var descReceived = new TaskCompletionSource<RTCSessionDescriptionInit>();

            using var neg = new PerfectNegotiator(
                pc, polite: true,
                sendDescription: desc => { descReceived.TrySetResult(desc); return Task.CompletedTask; },
                sendCandidate: _ => Task.CompletedTask);

            // Trigger negotiation by adding a data channel (both Browser and Desktop
            // fire needsNegotiation for this).
            using var dc = pc.CreateDataChannel("auto-neg-test");

            var done = await Task.WhenAny(descReceived.Task, Task.Delay(5000));
            if (done != descReceived.Task)
                throw new Exception("sendDescription never called within 5s of CreateDataChannel");

            var desc = await descReceived.Task;
            if (desc.Type != "offer")
                throw new Exception($"Expected offer type, got {desc.Type}");
            if (string.IsNullOrEmpty(desc.Sdp))
                throw new Exception("Offer SDP is empty");
            if (!neg.HasNegotiated)
                throw new Exception("HasNegotiated should be true after successful offer");
        }
    }
}
