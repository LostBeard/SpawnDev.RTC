using System;

namespace SpawnDev.RTC.Video
{
    /// <summary>
    /// Pulls pictures out of a received video track, so application code (face detection, a vision model, a recorder, a snapshot) can use the remote camera as
    /// pixels rather than as an opaque <see cref="IRTCMediaStreamTrack"/>. The video counterpart of <see cref="Audio.IRTCAudioReceiver"/>.
    /// <para>
    /// Desktop: taps the SipSorcery RTP receive path (depacketized H.264 access units) and decodes them with SpawnDev.MultiMedia's H.264 decoder
    /// (Media Foundation; Windows). Every frame is decoded - H.264 predicts from the frames before it - but only as many as <see cref="MaxFramesPerSecond"/> allows are
    /// converted to <see cref="RTCVideoFrame"/> and raised, so watching a 30 fps camera for a face once a second costs one colour conversion a second.
    /// </para>
    /// </summary>
    public interface IRTCVideoReceiver : IDisposable
    {
        /// <summary>Raised for each delivered picture. Handlers must not block: on desktop this runs on the RTP receive path.</summary>
        event Action<RTCVideoFrame>? OnFrame;

        /// <summary>Raised when a frame could not be decoded (a lost keyframe, an unsupported codec). The stream carries on.</summary>
        event Action<string>? OnError;

        /// <summary>The most frames per second delivered through <see cref="OnFrame"/>; 0 = every decoded frame. Default 5.</summary>
        double MaxFramesPerSecond { get; set; }

        /// <summary>Begins receiving. Idempotent.</summary>
        void Start();

        /// <summary>Stops receiving without disposing. Idempotent.</summary>
        void Stop();
    }
}
