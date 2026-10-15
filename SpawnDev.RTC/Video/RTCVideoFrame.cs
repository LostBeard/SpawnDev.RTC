namespace SpawnDev.RTC.Video
{
    /// <summary>
    /// One decoded picture from a received video track: 32-bit pixels in B, G, R, A byte order (what GDI+, WPF, WinForms, SkiaSharp and most vision pipelines take), top row first,
    /// no row padding. The platform-neutral currency of <see cref="IRTCVideoReceiver"/>.
    /// </summary>
    public readonly struct RTCVideoFrame
    {
        /// <summary><see cref="Width"/> x <see cref="Height"/> x 4 bytes, B G R A.</summary>
        public byte[] Bgra { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Milliseconds since the receiver started delivering frames (the sender's RTP clock is not wall time).</summary>
        public long TimestampMs { get; }

        public RTCVideoFrame(byte[] bgra, int width, int height, long timestampMs)
        {
            if (bgra is null) throw new System.ArgumentNullException(nameof(bgra));
            if (bgra.Length != width * height * 4) throw new System.ArgumentException($"{bgra.Length} bytes is not {width}x{height} BGRA.", nameof(bgra));
            Bgra = bgra; Width = width; Height = height; TimestampMs = timestampMs;
        }
    }
}
