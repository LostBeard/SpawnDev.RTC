using SpawnDev.RTC;
using SpawnDev.RTC.Desktop;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// The SCTP burst limit is a setting of the connection, for BOTH roles. Live 2026-10-15 a Reachy Mini data channel moved ~170 KB/s on a healthy LAN (plain TCP on the same
        /// link: 6.6 MB/s) because the sender may send 4 chunks per burst: <c>MaxBurst * 1300 B / RTT</c>. The side that gets the cookie creates its sender late, so a value set on the
        /// association before that is lost unless it is remembered; this checks it survives on the answerer too, and that the default is unchanged when nothing is asked.
        /// </summary>
        [TestMethod]
        public async Task Sctp_BurstSettings_ReachTheAssociation_OnBothSides_AndTheDefaultIsUnchanged()
        {
            if (OperatingSystem.IsBrowser()) return;

            using var offerer = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig { X_SctpMaxBurst = 32, X_SctpBurstPeriodMilliseconds = 10 });
            using var answerer = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig { X_SctpMaxBurst = 48 });
            using var plain = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig());
            offerer.OnIceCandidate += c => _ = answerer.AddIceCandidate(c);
            answerer.OnIceCandidate += c => _ = offerer.AddIceCandidate(c);

            var opened = new TaskCompletionSource<bool>();
            var channel = offerer.CreateDataChannel("tuning");
            channel.OnOpen += () => opened.TrySetResult(true);
            answerer.OnDataChannel += _ => { };

            var offer = await offerer.CreateOffer();
            await offerer.SetLocalDescription(offer);
            await answerer.SetRemoteDescription(offer);
            var answer = await answerer.CreateAnswer();
            await answerer.SetLocalDescription(answer);
            await offerer.SetRemoteDescription(answer);
            if (await Task.WhenAny(opened.Task, Task.Delay(20000)) != opened.Task) throw new Exception("the data channel never opened");

            var a = ((DesktopRTCPeerConnection)offerer).NativeConnection.sctp.RTCSctpAssociation;
            var b = ((DesktopRTCPeerConnection)answerer).NativeConnection.sctp.RTCSctpAssociation;
            var c = ((DesktopRTCPeerConnection)plain).NativeConnection.sctp.RTCSctpAssociation;
            if (a.MaxBurst != 32) throw new Exception($"offerer MaxBurst {a.MaxBurst}, expected 32");
            if (a.BurstPeriodMilliseconds != 10) throw new Exception($"offerer BurstPeriodMilliseconds {a.BurstPeriodMilliseconds}, expected 10");
            if (b.MaxBurst != 48) throw new Exception($"answerer MaxBurst {b.MaxBurst}, expected 48 (the side that gets the cookie creates its sender late)");
            if (b.BurstPeriodMilliseconds != 50) throw new Exception($"answerer BurstPeriodMilliseconds {b.BurstPeriodMilliseconds}, expected the default 50");
            if (c.MaxBurst != 4 || c.BurstPeriodMilliseconds != 50) throw new Exception($"an unconfigured connection must keep the RFC defaults, got {c.MaxBurst} / {c.BurstPeriodMilliseconds}");
        }
    }
}
