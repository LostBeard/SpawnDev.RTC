using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpawnDev.MultiMedia;

namespace SpawnDev.RTC.WpfDemo
{
    [SupportedOSPlatform("windows")]
    public class WpfVideoRenderer : IVideoRenderer
    {
        private IVideoTrack? _track;
        private WriteableBitmap? _bitmap;
        private bool _disposed;

        public WriteableBitmap? Bitmap => _bitmap;

        public bool IsAttached => _track != null;

        public event Action? OnFrameRendered;

        public void Attach(IVideoTrack track)
        {
            Detach();
            _track = track;
            _track.OnFrame += HandleFrame;
        }

        public void Detach()
        {
            if (_track != null)
            {
                _track.OnFrame -= HandleFrame;
                _track = null;
            }
        }

        private void HandleFrame(VideoFrame frame)
        {
            if (_disposed || frame.Width <= 0 || frame.Height <= 0) return;

            VideoFrame bgraFrame;
            if (frame.Format == VideoPixelFormat.BGRA)
                bgraFrame = frame;
            else
                bgraFrame = SpawnDev.MultiMedia.PixelFormatConverter.Convert(frame, VideoPixelFormat.BGRA);

            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                try
                {
                    if (_disposed) return;

                    if (_bitmap == null || _bitmap.PixelWidth != bgraFrame.Width || _bitmap.PixelHeight != bgraFrame.Height)
                    {
                        _bitmap = new WriteableBitmap(
                            bgraFrame.Width, bgraFrame.Height,
                            96, 96, PixelFormats.Bgra32, null);
                    }

                    _bitmap.Lock();
                    try
                    {
                        var data = bgraFrame.Data.Span;
                        unsafe
                        {
                            fixed (byte* src = data)
                            {
                                Buffer.MemoryCopy(src, _bitmap.BackBuffer.ToPointer(),
                                    _bitmap.BackBufferStride * _bitmap.PixelHeight,
                                    data.Length);
                            }
                        }
                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, bgraFrame.Width, bgraFrame.Height));
                    }
                    finally
                    {
                        _bitmap.Unlock();
                    }

                    OnFrameRendered?.Invoke();
                }
                catch
                {
                    // Frame rendering failed - skip this frame
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
