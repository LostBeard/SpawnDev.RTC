using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of media device access.
    /// Wraps navigator.mediaDevices via SpawnDev.BlazorJS.
    /// </summary>
    public static class BrowserMediaDevices
    {
        /// <summary>
        /// Converts our cross-platform MediaStreamConstraints to the BlazorJS type
        /// that maps to the W3C spec (Union&lt;bool, MediaTrackConstraints&gt;).
        /// </summary>
        private static SpawnDev.BlazorJS.JSObjects.MediaStreamConstraints ToBlazorJS(MediaStreamConstraints constraints)
        {
            var result = new SpawnDev.BlazorJS.JSObjects.MediaStreamConstraints();
            if (constraints.Audio != null)
            {
                if (constraints.Audio.IsBool)
                    result.Audio = constraints.Audio.BoolValue!.Value;
                else if (constraints.Audio.Constraints != null)
                    result.Audio = ToBlazorJSTrackConstraints(constraints.Audio.Constraints);
            }
            if (constraints.Video != null)
            {
                if (constraints.Video.IsBool)
                    result.Video = constraints.Video.BoolValue!.Value;
                else if (constraints.Video.Constraints != null)
                    result.Video = ToBlazorJSTrackConstraints(constraints.Video.Constraints);
            }
            return result;
        }

        private static SpawnDev.BlazorJS.JSObjects.MediaTrackConstraints ToBlazorJSTrackConstraints(MediaTrackConstraints c)
        {
            var result = new SpawnDev.BlazorJS.JSObjects.MediaTrackConstraints();
            if (c.DeviceId != null) result.DeviceId = c.DeviceId;
            if (c.Width != null) result.Width = (ulong)c.Width.Value;
            if (c.Height != null) result.Height = (ulong)c.Height.Value;
            if (c.FrameRate != null) result.FrameRate = c.FrameRate.Value;
            if (c.FacingMode != null) result.FacingMode = c.FacingMode;
            if (c.SampleRate != null) result.SampleRate = (ulong)c.SampleRate.Value;
            if (c.ChannelCount != null) result.ChannelCount = (ulong)c.ChannelCount.Value;
            if (c.EchoCancellation != null) result.EchoCancellation = c.EchoCancellation.Value;
            if (c.NoiseSuppression != null) result.NoiseSuppression = c.NoiseSuppression.Value;
            if (c.AutoGainControl != null) result.AutoGainControl = c.AutoGainControl.Value;
            return result;
        }

        public static async Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            var JS = BlazorJSRuntime.JS;
            using var navigator = JS.Get<Navigator>("navigator");
            using var mediaDevices = navigator.MediaDevices;
            var blazorConstraints = ToBlazorJS(constraints);
            var stream = await mediaDevices.GetUserMedia(blazorConstraints);
            if (stream == null) throw new InvalidOperationException("GetUserMedia returned null");
            return new BrowserRTCMediaStream(stream);
        }

        public static async Task<IRTCMediaStream> GetDisplayMedia(MediaStreamConstraints? constraints)
        {
            var JS = BlazorJSRuntime.JS;
            using var navigator = JS.Get<Navigator>("navigator");
            using var mediaDevices = navigator.MediaDevices;
            MediaStream? stream;
            if (constraints != null)
            {
                var blazorConstraints = ToBlazorJS(constraints);
                stream = await mediaDevices.GetDisplayMedia(blazorConstraints);
            }
            else
            {
                stream = await mediaDevices.GetDisplayMedia();
            }
            if (stream == null) throw new InvalidOperationException("GetDisplayMedia returned null");
            return new BrowserRTCMediaStream(stream);
        }

        public static async Task<RTCMediaDeviceInfo[]> EnumerateDevices()
        {
            var JS = BlazorJSRuntime.JS;
            using var navigator = JS.Get<Navigator>("navigator");
            using var mediaDevices = navigator.MediaDevices;
            var devices = await mediaDevices.EnumerateDevices();
            var result = new RTCMediaDeviceInfo[devices.Length];
            for (var i = 0; i < devices.Length; i++)
            {
                using var device = devices[i];
                result[i] = new RTCMediaDeviceInfo
                {
                    DeviceId = device.DeviceId,
                    Kind = device.Kind,
                    Label = device.Label,
                    GroupId = device.GroupId,
                };
            }
            return result;
        }
    }
}
