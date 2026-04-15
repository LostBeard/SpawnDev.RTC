using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of media device access.
    /// Creates SipSorcery media tracks with standard codec capabilities.
    /// For real device capture (cameras, microphones), use NativeConnection
    /// with SIPSorceryMedia.Windows or SIPSorceryMedia.FFmpeg packages.
    /// </summary>
    public static class DesktopMediaDevices
    {
        public static Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            var tracks = new List<IRTCMediaStreamTrack>();

            if (constraints.Audio != null)
            {
                var audioFormats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                };
                var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, audioFormats, MediaStreamStatusEnum.SendRecv);
                tracks.Add(new DesktopRTCMediaStreamTrack(audioTrack));
            }

            if (constraints.Video != null)
            {
                var videoFormats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)),
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 100)),
                };
                var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, videoFormats, MediaStreamStatusEnum.SendRecv);
                tracks.Add(new DesktopRTCMediaStreamTrack(videoTrack));
            }

            if (tracks.Count == 0)
                throw new ArgumentException("At least one of audio or video must be requested.");

            IRTCMediaStream stream = new DesktopRTCMediaStream(tracks.ToArray());
            return Task.FromResult(stream);
        }
    }
}
