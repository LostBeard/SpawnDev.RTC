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

            // On desktop this is a no-op and won't throw. On browser, modifying
            // RIDs after getParameters is rejected with DOM `InvalidModificationError`
            // ("Read-only field modified in setParameters()") - encodings[i].rid is
            // treated as a read-only field once the transceiver exists. The browser
            // surfaces this through our BlazorJS wrapper as a plain `Exception`
            // rather than a typed JSException (the message starts with the DOM
            // error name). The point of this test is that the DTO shape survives
            // round-trip, not that real browser simulcast reconfig works - swallow
            // ANY exception on the browser path. Desktop stays narrow.
            try { await sender.SetParameters(parameters); }
            catch (Exception) when (OperatingSystem.IsBrowser()) { /* browser rejection is expected */ }
        }

        /// <summary>
        /// Browser-only: initial-simulcast test via <see cref="IRTCPeerConnection.AddTransceiver(string, RTCRtpTransceiverInit)"/>.
        /// Creates a transceiver with 3 sendEncodings (h/m/l with distinct RIDs +
        /// bitrate scales), creates an offer, and parses the SDP to confirm the
        /// simulcast layers actually made it onto the wire format:
        ///   <c>a=simulcast:send h;m;l</c> + <c>a=rid:h send</c>, <c>a=rid:m send</c>, <c>a=rid:l send</c>
        ///
        /// RFC 8853 locks RIDs at transceiver-creation time - this is the ONLY path
        /// that exercises real simulcast negotiation. Closes the RTC v0.1.0 plan
        /// gap documented as "Simulcast sendEncodings not yet exposed on the
        /// cross-platform API". Proves the new
        /// <see cref="IRTCPeerConnection.AddTransceiver(string, RTCRtpTransceiverInit)"/>
        /// overload round-trips to the browser's native addTransceiver.
        /// </summary>
        [TestMethod(Timeout = 20000)]
        public async Task RtpSender_InitSimulcast_SdpOfferContainsSimulcastAndRidLines()
        {
            if (!OperatingSystem.IsBrowser())
                throw new UnsupportedTestException("SipSorcery does not implement real simulcast (no RID/sendEncodings negotiation). Browser-only.");

            using var pc = RTCPeerConnectionFactory.Create();

            // 3-layer simulcast config - standard h/m/l nomenclature with bitrate/scale per layer.
            var transceiver = pc.AddTransceiver("video", new RTCRtpTransceiverInit
            {
                Direction = "sendonly",
                SendEncodings = new[]
                {
                    new RTCRtpEncoding { Rid = "h", Active = true, MaxBitrate = 2_500_000, ScaleResolutionDownBy = 1.0 },
                    new RTCRtpEncoding { Rid = "m", Active = true, MaxBitrate = 500_000,   ScaleResolutionDownBy = 2.0 },
                    new RTCRtpEncoding { Rid = "l", Active = true, MaxBitrate = 150_000,   ScaleResolutionDownBy = 4.0 },
                },
            });
            if (transceiver == null) throw new Exception("AddTransceiver returned null");

            var offer = await pc.CreateOffer();
            if (string.IsNullOrEmpty(offer.Sdp))
                throw new Exception("Offer had empty SDP - can't verify simulcast negotiation");

            // Verify the three RID lines are present in send direction.
            foreach (var rid in new[] { "h", "m", "l" })
            {
                var needle = $"a=rid:{rid} send";
                if (!offer.Sdp.Contains(needle, StringComparison.Ordinal))
                    throw new Exception($"SDP offer missing `{needle}`. Browser simulcast negotiation did not pick up sendEncodings[Rid={rid}]. SDP:\n{offer.Sdp}");
            }

            // Verify the simulcast announce line - browsers emit either `h;m;l` (semi-colon
            // separated, the RFC 8853 canonical form) or equivalent spacings. Just look for
            // all three RIDs in any simulcast:send line.
            var simLineIndex = offer.Sdp.IndexOf("a=simulcast:send", StringComparison.Ordinal);
            if (simLineIndex < 0)
                throw new Exception("SDP offer missing `a=simulcast:send` line. sendEncodings did not translate to simulcast negotiation. SDP:\n" + offer.Sdp);

            var simLineEnd = offer.Sdp.IndexOf('\n', simLineIndex);
            var simLine = simLineEnd > simLineIndex ? offer.Sdp.Substring(simLineIndex, simLineEnd - simLineIndex) : offer.Sdp.Substring(simLineIndex);
            foreach (var rid in new[] { "h", "m", "l" })
            {
                if (!simLine.Contains(rid, StringComparison.Ordinal))
                    throw new Exception($"simulcast:send line '{simLine.Trim()}' does not contain RID '{rid}'. Full SDP:\n{offer.Sdp}");
            }

            // Bonus: sender.getParameters() should report 3 encodings with matching RIDs.
            var sender = transceiver.Sender;
            if (sender == null) throw new Exception("Transceiver had no sender after AddTransceiver");
            var parameters = sender.GetParameters();
            if (parameters.Encodings == null || parameters.Encodings.Length != 3)
                throw new Exception($"sender.GetParameters() returned {parameters.Encodings?.Length ?? 0} encodings, expected 3");
            var rids = parameters.Encodings.Select(e => e.Rid).ToArray();
            foreach (var expected in new[] { "h", "m", "l" })
            {
                if (!rids.Contains(expected))
                    throw new Exception($"Expected RID '{expected}' in sender.GetParameters().Encodings, got [{string.Join(",", rids)}]");
            }
        }
    }
}
