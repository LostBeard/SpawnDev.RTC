namespace SpawnDev.RTC.Audio
{
    /// <summary>
    /// One block of interleaved PCM16 audio - the platform-neutral currency of the
    /// <see cref="IRTCAudioSender"/> / <see cref="IRTCAudioReceiver"/> bridge.
    /// <para>
    /// This is deliberately the lowest-common-denominator shape both platforms can produce and consume
    /// without a codec: the browser reads/writes it through WebCodecs <c>AudioData</c> (Insertable
    /// Streams), and the desktop reads/writes it through the SipSorcery <c>AudioEncoder</c> (Opus). A
    /// consumer that wants a different rate/channel layout resamples on its own side - the bridge does
    /// not hide a resampler, so the samples that cross are exactly the ones captured or produced.
    /// </para>
    /// </summary>
    public readonly struct AudioPcmFrame
    {
        /// <summary>Interleaved signed 16-bit PCM. Length == <see cref="SampleCount"/> * <see cref="Channels"/>.</summary>
        public short[] Pcm { get; }

        /// <summary>Samples per second (e.g. 48000 for WebRTC Opus, 16000 for a Whisper feed).</summary>
        public int SampleRate { get; }

        /// <summary>Channel count (1 = mono, 2 = interleaved stereo).</summary>
        public int Channels { get; }

        /// <summary>Number of sample frames (per-channel samples). <c>Pcm.Length / Channels</c>.</summary>
        public int SampleCount => Channels > 0 ? Pcm.Length / Channels : 0;

        /// <summary>
        /// Creates a PCM frame. <paramref name="pcm"/> is held by reference (not copied); the caller
        /// must not mutate it after handing it over.
        /// </summary>
        public AudioPcmFrame(short[] pcm, int sampleRate, int channels)
        {
            Pcm = pcm ?? throw new System.ArgumentNullException(nameof(pcm));
            if (sampleRate <= 0) throw new System.ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new System.ArgumentOutOfRangeException(nameof(channels));
            SampleRate = sampleRate;
            Channels = channels;
        }
    }
}
