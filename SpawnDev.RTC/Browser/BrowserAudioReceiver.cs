using System;
using System.Threading;
using System.Threading.Tasks;
using SpawnDev.RTC.Audio;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser <see cref="IRTCAudioReceiver"/>: runs a received audio track through a
    /// <c>MediaStreamTrackProcessor</c> and reads the resulting WebCodecs <c>AudioData</c> frames,
    /// converting each to interleaved PCM16. The browser already decoded Opus, so this is a format
    /// normalization (planar/interleaved, f32/s16) rather than a codec.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    public sealed class BrowserAudioReceiver : IRTCAudioReceiver
    {
        private readonly MediaStreamTrackProcessor _processor;
        private readonly ReadableStreamDefaultReader _reader;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private bool _disposed;

        public event Action<AudioPcmFrame>? OnPcmFrame;

        public BrowserAudioReceiver(BrowserRTCMediaStreamTrack track)
        {
            if (track is null) throw new ArgumentNullException(nameof(track));
            _processor = new MediaStreamTrackProcessor(new MediaStreamTrackProcessorOptions { Track = track.NativeTrack });
            _reader = _processor.Readable.GetReader();
        }

        public void Start()
        {
            if (_disposed || _loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = ReadLoop(_cts.Token);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async Task ReadLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ReadableStreamReaderReadResponse res;
                try { res = await _reader.Read(); }
                catch { break; }
                if (res.Done) { res.Dispose(); break; }

                // The chunk of an audio MediaStreamTrackProcessor is an AudioData, not a byte view -
                // read it with the correct wrapper type rather than the reader's byte-typed Value.
                var audioData = res.JSRef!.Get<AudioData?>("value");
                res.Dispose();
                if (audioData is null) continue;

                try
                {
                    var frame = await ToInterleavedS16(audioData);
                    if (frame.Pcm.Length > 0) OnPcmFrame?.Invoke(frame);
                }
                catch { /* a bad frame must not kill the stream */ }
                finally
                {
                    try { audioData.Close(); } catch { }
                    audioData.Dispose();
                }
            }
        }

        // Normalizes an AudioData of any WebCodecs sample format to one interleaved PCM16 frame.
        // Async because AudioData.CopyTo is awaited (blocking the single WASM thread would deadlock).
        private static async Task<AudioPcmFrame> ToInterleavedS16(AudioData audioData)
        {
            int frames = audioData.NumberOfFrames;
            int channels = audioData.NumberOfChannels;
            int rate = (int)audioData.SampleRate;
            string fmt = audioData.Format ?? "f32-planar";
            bool planar = fmt.EndsWith("-planar", StringComparison.Ordinal);
            var pcm = new short[frames * channels];

            if (!planar)
            {
                // One interleaved plane holds every channel's samples.
                if (fmt.StartsWith("s16", StringComparison.Ordinal))
                {
                    using var dest = new Int16Array(frames * channels);
                    await CopyPlane(audioData, dest, 0);
                    var s = dest.ToArray();
                    System.Array.Copy(s, pcm, Math.Min(s.Length, pcm.Length));
                }
                else if (fmt.StartsWith("f32", StringComparison.Ordinal))
                {
                    using var dest = new Float32Array(frames * channels);
                    await CopyPlane(audioData, dest, 0);
                    var f = dest.ToArray();
                    for (int i = 0; i < pcm.Length && i < f.Length; i++) pcm[i] = FloatToS16(f[i]);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported interleaved AudioData format '{fmt}'.");
                }
            }
            else
            {
                // One plane per channel; interleave into the output.
                for (int c = 0; c < channels; c++)
                {
                    if (fmt.StartsWith("s16", StringComparison.Ordinal))
                    {
                        using var dest = new Int16Array(frames);
                        await CopyPlane(audioData, dest, c);
                        var s = dest.ToArray();
                        for (int i = 0; i < frames && i < s.Length; i++) pcm[i * channels + c] = s[i];
                    }
                    else if (fmt.StartsWith("f32", StringComparison.Ordinal))
                    {
                        using var dest = new Float32Array(frames);
                        await CopyPlane(audioData, dest, c);
                        var f = dest.ToArray();
                        for (int i = 0; i < frames && i < f.Length; i++) pcm[i * channels + c] = FloatToS16(f[i]);
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported planar AudioData format '{fmt}'.");
                    }
                }
            }

            return new AudioPcmFrame(pcm, rate, channels);
        }

        private static Task CopyPlane(AudioData audioData, TypedArray dest, int planeIndex)
            => audioData.CopyTo(dest, new AudioDataCopyToOptions { PlaneIndex = planeIndex });

        private static short FloatToS16(float f)
        {
            int v = (int)MathF.Round(f * 32767f);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            return (short)v;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _reader.Dispose();
            _processor.Dispose();
            _cts?.Dispose();
        }
    }
}
