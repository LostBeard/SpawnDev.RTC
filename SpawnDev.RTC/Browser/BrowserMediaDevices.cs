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
            var jsConstraints = BuildJsConstraints(constraints);
            var stream = await mediaDevices.GetUserMedia(jsConstraints);
            return new BrowserRTCMediaStream(stream);
        }

        public static async Task<IRTCMediaStream> GetDisplayMedia(MediaStreamConstraints? constraints)
        {
            var JS = BlazorJSRuntime.JS;
            using var navigator = JS.Get<Navigator>("navigator");
            using var mediaDevices = navigator.MediaDevices;
            MediaStream stream;
            if (constraints != null)
            {
                var jsConstraints = BuildJsConstraints(constraints);
                stream = await mediaDevices.JSRef!.CallAsync<MediaStream>("getDisplayMedia", jsConstraints);
            }
            else
            {
                stream = await mediaDevices.JSRef!.CallAsync<MediaStream>("getDisplayMedia");
            }
            return new BrowserRTCMediaStream(stream);
        }

        private static MediaStreamConstraints BuildJsConstraints(MediaStreamConstraints constraints)
        {
            // The constraints class is JSON-serializable and matches the browser API
            // so we can pass it directly
            return constraints;
        }
    }
}
