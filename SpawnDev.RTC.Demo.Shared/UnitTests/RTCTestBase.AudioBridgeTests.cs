using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpawnDev.RTC;
using SpawnDev.RTC.Audio;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    /// <summary>
    /// Tests for the cross-platform PCM audio bridge (<see cref="IRTCAudioSender"/> /
    /// <see cref="IRTCAudioReceiver"/>). No mocks: the desktop test runs real Opus encode+decode through
    /// the same SipSorcery path the bridge uses; the browser test runs a real MediaStreamTrackGenerator ->
    /// MediaStreamTrackProcessor loopback through the actual bridge classes.
    /// </summary>
    public abstract partial class RTCTestBase
    {
        /// <summary>Cross-platform: the PCM frame value type counts frames and validates its inputs.</summary>
        [TestMethod]
        public async Task AudioBridge_PcmFrame_ValidatesAndCounts()
        {
            var f = new AudioPcmFrame(new short[8], 48000, 2);
            if (f.SampleCount != 4) throw new Exception($"SampleCount {f.SampleCount} != 4");
            if (f.SampleRate != 48000 || f.Channels != 2) throw new Exception("rate/channels not preserved");

            bool threw = false;
            try { _ = new AudioPcmFrame(new short[4], 0, 1); } catch (ArgumentOutOfRangeException) { threw = true; }
            if (!threw) throw new Exception("expected non-positive sample rate to throw");

            threw = false;
            try { _ = new AudioPcmFrame(null!, 48000, 1); } catch (ArgumentNullException) { threw = true; }
            if (!threw) throw new Exception("expected null pcm to throw");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Desktop: pushing PCM through the trackless MultiMediaAudioSource (what DesktopAudioSender uses)
        /// produces Opus that AudioEncoder.DecodeAudio (what DesktopAudioReceiver uses) turns back into PCM
        /// of comparable energy. Real Concentus Opus, lossy - so this asserts energy is preserved within a
        /// broad band, not bit-equality.
        /// </summary>
        [TestMethod]
        public async Task AudioBridge_Desktop_OpusEncodeDecode_RoundTripsEnergy()
        {
            if (OperatingSystem.IsBrowser()) return;

            using var source = new SpawnDev.RTC.Desktop.MultiMediaAudioSource();
            var fmt = source.GetAudioSourceFormats()[0]; // Opus WebRTC (48 kHz)
            int rate = fmt.ClockRate;
            int channels = fmt.ChannelCount > 0 ? fmt.ChannelCount : 1;

            using var decoder = new SIPSorcery.Media.AudioEncoder(
                SIPSorceryMedia.Abstractions.AudioCommonlyUsedFormats.OpusWebRTC);

            var decoded = new List<short>();
            source.OnAudioSourceEncodedSample += (durationRtpUnits, encoded) =>
            {
                var pcm = decoder.DecodeAudio(encoded, fmt);
                if (pcm != null) decoded.AddRange(pcm);
            };

            // 200 ms of 440 Hz sine, pushed as 20 ms interleaved chunks (Opus frame granularity).
            var input = MakeSine(rate / 5, channels, rate, 440);
            int chunkSamples = (rate / 50) * channels;
            for (int off = 0; off < input.Length; off += chunkSamples)
            {
                int len = Math.Min(chunkSamples, input.Length - off);
                var part = new short[len];
                System.Array.Copy(input, off, part, 0, len);
                source.PushPcm(part);
            }

            if (decoded.Count == 0)
                throw new Exception("Opus encode+decode produced no PCM - the desktop bridge codec path is broken.");

            double inRms = Rms(input);
            double outRms = Rms(decoded.ToArray());
            if (inRms <= 0) throw new Exception("test input was silent");
            double ratio = outRms / inRms;
            if (ratio < 0.3 || ratio > 3.0)
                throw new Exception($"decoded RMS {outRms:F0} not within a lossy-Opus band of input RMS {inRms:F0} " +
                                    $"(ratio {ratio:F2}); decoded {decoded.Count} samples.");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Browser: a BrowserAudioSender's MediaStreamTrackGenerator, read straight back through a
        /// BrowserAudioReceiver's MediaStreamTrackProcessor, returns the pushed audio as PCM. Exercises the
        /// full PCM -> AudioData -> track -> AudioData -> PCM path (incl. the receiver's format
        /// normalization) with no peer connection.
        /// </summary>
        [TestMethod]
        public async Task AudioBridge_Browser_GeneratorToProcessor_RoundTrips()
        {
            if (!OperatingSystem.IsBrowser()) return;

            const int rate = 48000, channels = 1;
            using var sender = new SpawnDev.RTC.Browser.BrowserAudioSender(rate, channels);
            var track = (SpawnDev.RTC.Browser.BrowserRTCMediaStreamTrack)sender.Track;
            using var receiver = new SpawnDev.RTC.Browser.BrowserAudioReceiver(track);

            var tcs = new TaskCompletionSource<AudioPcmFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.OnPcmFrame += frame => tcs.TrySetResult(frame);
            receiver.Start();

            // Push several 20 ms frames so the processor is guaranteed to surface at least one.
            var sine = MakeSine(rate / 50, channels, rate, 440);
            for (int i = 0; i < 5; i++)
                sender.PushPcm(new AudioPcmFrame((short[])sine.Clone(), rate, channels));

            var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
            if (done != tcs.Task)
                throw new Exception("browser generator->processor produced no PCM frame within 10s.");

            var got = tcs.Task.Result;
            if (got.SampleRate != rate) throw new Exception($"round-trip sample rate {got.SampleRate} != {rate}");
            if (got.Channels != channels) throw new Exception($"round-trip channels {got.Channels} != {channels}");
            if (Rms(got.Pcm) < 1.0) throw new Exception("round-trip PCM came back silent");
        }

        // 440 Hz-ish sine, interleaved to the requested channel count, PCM16.
        private static short[] MakeSine(int frames, int channels, int rate, double hz)
        {
            var a = new short[frames * channels];
            for (int i = 0; i < frames; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 10000);
                for (int c = 0; c < channels; c++) a[i * channels + c] = s;
            }
            return a;
        }

        private static double Rms(short[] a)
        {
            if (a.Length == 0) return 0;
            double sum = 0;
            foreach (var s in a) sum += (double)s * s;
            return Math.Sqrt(sum / a.Length);
        }
    }
}
