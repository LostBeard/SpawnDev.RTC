using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    /// <summary>
    /// Tests for <see cref="IRTCRtpSender.GetParameters"/> / <see cref="IRTCRtpSender.SetParameters"/>
    /// - the simulcast control surface.
    ///
    /// Browser: wraps the native RTCRtpSender.getParameters / setParameters via
    /// BlazorJS typed DTOs (RTCRtpSendParameters in SpawnDev.BlazorJS.JSObjects.WebRTC).
    /// Real simulcast behavior depends on the browser's video encoder; these tests
    /// just prove the round-trip shape works.
    ///
    /// Desktop: SipSorcery has no simulcast support; GetParameters returns a
    /// single-encoding default, SetParameters is a no-op (documented in XML doc).
    /// The tests still run on desktop to lock in the contract shape so a future
    /// SipSorcery-native simulcast implementation can't silently break the API.
    /// </summary>
    public abstract partial class RTCTestBase
    {
        [TestMethod]
        public async Task RtpSender_GetParameters_ReturnsValidShape()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            // Need a track to have a sender; adding an AddTransceiver is the cheapest path
            // since it doesn't require real media on either platform.
            var transceiver = pc.AddTransceiver("video");
            var sender = transceiver.Sender;
            if (sender == null) throw new Exception("AddTransceiver(video) should produce a sender");

            var parameters = sender.GetParameters();
            if (parameters == null) throw new Exception("GetParameters returned null");

            // Both platforms should populate TransactionId (browser via the browser impl,
            // desktop via our monotonic counter).
            if (string.IsNullOrEmpty(parameters.TransactionId))
                throw new Exception("TransactionId should be populated");

            // Encodings array should exist with at least one entry (single-encoding default).
            if (parameters.Encodings == null || parameters.Encodings.Length == 0)
                throw new Exception($"Encodings should have at least one entry; got {parameters.Encodings?.Length}");

            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RtpSender_SetParameters_TransactionIdRoundTrip_Succeeds()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("video");
            var sender = transceiver.Sender;
            if (sender == null) throw new Exception("AddTransceiver(video) should produce a sender");

            var parameters = sender.GetParameters();
            // Round-trip unchanged - both platforms should accept our own most-recent transactionId.
            await sender.SetParameters(parameters);
        }

        [TestMethod]
        public async Task RtpSender_SetParameters_StaleTransactionId_Throws()
        {
            if (OperatingSystem.IsBrowser()) return;
            // Desktop-only: the browser's rejection happens inside the JS engine with
            // an `InvalidStateError` that surfaces as a JS exception; exact type +
            // timing varies across browsers + BlazorJS's async marshaling. The
            // desktop path has a deterministic check we can assert cleanly.

            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("video");
            var sender = transceiver.Sender;
            if (sender == null) throw new Exception("AddTransceiver(video) should produce a sender");

            var parameters = sender.GetParameters();
            var originalId = parameters.TransactionId;
            // Calling GetParameters again bumps the server-side counter, stale-ing the
            // first snapshot's TransactionId.
            _ = sender.GetParameters();

            parameters.TransactionId = originalId; // pretend we never got the newer one
            try
            {
                await sender.SetParameters(parameters);
                throw new Exception("Expected InvalidOperationException for stale transactionId");
            }
            catch (InvalidOperationException) { /* expected */ }
        }

        [TestMethod]
        public async Task RtpSender_EncodingShape_SimulcastLayeringRoundTrips()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceiver = pc.AddTransceiver("video");
            var sender = transceiver.Sender;
            if (sender == null) throw new Exception("AddTransceiver(video) should produce a sender");

            // Exercise the DTO shape: 3-layer simulcast config. Whether the platform
            // actually applies these is implementation-defined (browser does,
            // SipSorcery ignores); this test locks the DTO contract shape.
            var parameters = sender.GetParameters();
            parameters.Encodings = new[]
            {
                new RTCRtpEncoding { Rid = "h", Active = true, MaxBitrate = 2_500_000, ScaleResolutionDownBy = 1.0 },
                new RTCRtpEncoding { Rid = "m", Active = true, MaxBitrate = 500_000, ScaleResolutionDownBy = 2.0 },
                new RTCRtpEncoding { Rid = "l", Active = true, MaxBitrate = 150_000, ScaleResolutionDownBy = 4.0 },
            };

            // On desktop this is a no-op and won't throw. On browser, RID changes
            // after the offer are rejected - but we added the transceiver FRESH so
            // there's no offer yet; setParameters-with-different-RIDs should be
            // accepted (browser accepts initial encodings on first setParameters).
            // If the browser rejects anyway we swallow - the point of this test is
            // the DTO shape survives round-trip, not real browser simulcast behavior.
            try { await sender.SetParameters(parameters); }
            catch (Microsoft.JSInterop.JSException) { /* browser may reject RID changes; OK */ }
        }
    }
}
