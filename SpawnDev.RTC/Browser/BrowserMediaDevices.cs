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
        public static async Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            var JS = BlazorJSRuntime.JS;
            using var navigator = JS.Get<Navigator>("navigator");
            using var mediaDevices = navigator.MediaDevices;
            var stream = await mediaDevices.GetUserMedia(constraints);
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
                stream = await mediaDevices.GetDisplayMedia(constraints);
            }
            else
            {
                stream = await mediaDevices.GetDisplayMedia(new { });
            }
            if (stream == null) throw new InvalidOperationException("GetDisplayMedia returned null");
            return new BrowserRTCMediaStream(stream);
        }
    }
}
