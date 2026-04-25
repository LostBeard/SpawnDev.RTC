using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.DemoConsole.UnitTests
{
    /// <summary>
    /// Verifies SpawnDev-fork API surface deltas in the bundled SipSorcery submodule.
    /// These tests exist to catch regressions if the fork is ever rebased / re-forked
    /// and a manual `internal` -> `public` change is dropped on the floor.
    ///
    /// Each fork modification gets one test that simply uses the new affordance from
    /// outside the SIPSorcery assembly. If the visibility regresses, these tests
    /// stop compiling, not fail at runtime - which is exactly the signal we want
    /// (the API contract is enforced at the type-checker level).
    ///
    /// Backlog of fork modifications: <c>Src/sipsorcery/UPSTREAM_BACKLOG.md</c>.
    /// </summary>
    public class DesktopForkApiTests
    {
        // ---------------------------------------------------------------------
        // Fork: MediaStreamTrack.StreamStatus public setter (rc.5, 2026-04-25)
        // Upstream surface had `internal set;` which forced consumers to either
        // recreate the track or use reflection to flip direction post-construction.
        // ---------------------------------------------------------------------

        [TestMethod]
        public Task MediaStreamTrack_StreamStatus_PublicSetterWorks()
        {
            var format = new AudioFormat(AudioCodecsEnum.PCMU, 0);
            var track = new MediaStreamTrack(format, MediaStreamStatusEnum.SendRecv);
            if (track.StreamStatus != MediaStreamStatusEnum.SendRecv)
                throw new Exception($"Initial status should be SendRecv, got {track.StreamStatus}");

            // The whole point of the fork delta: the setter is PUBLIC. If anyone
            // ever regresses the visibility back to `internal`, the next two
            // assignments stop compiling from this assembly (different assembly
            // than SIPSorcery itself), which is exactly the signal we want.
            track.StreamStatus = MediaStreamStatusEnum.RecvOnly;
            if (track.StreamStatus != MediaStreamStatusEnum.RecvOnly)
                throw new Exception($"Status should be RecvOnly after assignment, got {track.StreamStatus}");

            track.StreamStatus = MediaStreamStatusEnum.Inactive;
            if (track.StreamStatus != MediaStreamStatusEnum.Inactive)
                throw new Exception($"Status should round-trip through Inactive, got {track.StreamStatus}");

            track.StreamStatus = MediaStreamStatusEnum.SendOnly;
            if (track.StreamStatus != MediaStreamStatusEnum.SendOnly)
                throw new Exception($"Status should round-trip through SendOnly, got {track.StreamStatus}");

            // DefaultStreamStatus is unchanged - still the original construction-time value.
            // Verifies we didn't accidentally widen the wrong setter.
            if (track.DefaultStreamStatus != MediaStreamStatusEnum.SendRecv)
                throw new Exception($"DefaultStreamStatus should still be SendRecv (not affected by setter), got {track.DefaultStreamStatus}");

            return Task.CompletedTask;
        }
    }
}
