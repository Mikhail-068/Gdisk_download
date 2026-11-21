using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DriveDownload;

internal static class WindowBackdrop
{
    private const double TextureScale = 0.5;

    internal static void Apply(Window window)
    {
        var brush = ((DrawingBrush)window.FindResource("CanvasBrush")).CloneCurrentValue();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source?.CompositionTarget is { } target)
        {
            var device = target.TransformToDevice;
            SetViewport(brush, new DpiScale(device.M11, device.M22));
        }
        else
        {
            SetViewport(brush, VisualTreeHelper.GetDpi(window));
        }
        window.Background = brush;
        window.DpiChanged += (_, args) => SetViewport(brush, args.NewDpi);
    }

    private static void SetViewport(DrawingBrush brush, DpiScale dpi)
    {
        var image = brush.Viewbox;
        brush.Viewport = new Rect(0, 0, image.Width * TextureScale / dpi.DpiScaleX, image.Height * TextureScale / dpi.DpiScaleY);
    }
}