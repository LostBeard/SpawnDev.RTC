using System;
using System.Threading.Tasks;
using SpawnDev.RTC.Audio;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser <see cref="IRTCAudioSender"/>: turns pushed PCM into a live audio track using the
    /// Insertable Streams API. A <c>MediaStreamTrackGenerator</c> IS a <c>MediaStreamTrack</c>, so its
    /// output is the sendable track; each <see cref="PushPcm"/> builds a WebCodecs <c>AudioData</c>
    /// (interleaved s16) and writes it to the generator's writable stream. The browser's own WebRTC
    /// stack Opus-encodes the track - no managed codec involved.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public sealed class BrowserAudioSender : IRTCAudioSender
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly MediaStreamTrackGenerator _generator;
        private readonly WritableStreamDefaultWriter _writer;
        private readonly BrowserRTCMediaStreamTrack _track;

        // Running per-channel sample position, converted to the microsecond timestamp each AudioData
        // needs. WebCodecs uses the timestamp for ordering/AV-sync, so it must advance monotonically.
        private long _samplePos;
        // Serializes writes so two write() calls never overlap on the one writer.
        private Task _writeChain = Task.CompletedTask;
        private bool _disposed;

        public BrowserAudioSender(int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            _sampleRate = sampleRate;
            _channels = channels;
            _generator = new MediaStreamTrackGenerator(new MediaStreamTrackGeneratorOptions { Kind = "audio" });
            _writer = _generator.Writable.GetWriter();
            _track = new BrowserRTCMediaStreamTrack(_generator);
        }

        public IRTCMediaStreamTrack Track => _track;

        public void PushPcm(AudioPcmFrame frame)
        {
            if (_disposed) return;
            if (frame.Channels != _channels || frame.SampleRate != _sampleRate)
                throw new ArgumentException(
                    $"Frame is {frame.SampleRate} Hz / {frame.Channels} ch but this sender was created for " +
                    $"{_sampleRate} Hz / {_channels} ch. The bridge does not resample - convert before pushing.");

            var frames = frame.SampleCount;
            if (frames == 0) return;

            // Cross the interleaved PCM into a JS Int16Array, then wrap it as an AudioData. AudioData copies
            // the samples out of the source array on construction, so the Int16Array can be freed right after.
            var timestampUs = _samplePos * 1_000_000L / _sampleRate;
            _samplePos += frames;

            var jsPcm = new Int16Array(frame.Pcm);
            var audioData = new AudioData(new AudioDataOptions
            {
                Format = "s16",
                SampleRate = _sampleRate,
                NumberOfFrames = frames,
                NumberOfChannels = _channels,
                Timestamp = timestampUs,
                Data = jsPcm,
            });
            jsPcm.Dispose();

            // Chain the async write so writes never overlap; close the AudioData once it is consumed.
            _writeChain = _writeChain.ContinueWith(_ => WriteOne(audioData)).Unwrap();
        }

        private async Task WriteOne(AudioData audioData)
        {
            try
            {
                await _writer.Ready;
                await _writer.JSRef!.CallVoidAsync("write", audioData);
            }
            catch { /* a dropped frame must not kill the stream */ }
            finally
            {
                try { audioData.Close(); } catch { }
                audioData.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ = _writer.Close(); } catch { }
            _writer.Dispose();
            _track.Dispose();
            _generator.Dispose();
        }
    }
}
