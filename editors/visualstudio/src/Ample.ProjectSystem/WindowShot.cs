// menu handlers run on the UI thread
#pragma warning disable VSTHRD010, VSSDK007
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace Ample.VisualStudio;

/// <summary>
/// "AMPLE: Save a picture of this window": draws the Visual Studio main window (WPF) into a PNG from the inside. The Windows capture calls
/// (PrintWindow, screen copy) give a white window for Visual Studio's GPU-composed surface and a screen copy also shows other windows;
/// drawing the visual tree reads nothing but this window. The file is %TEMP%\ample-vs-shot.png, or the path in AMPLE_VS_SHOT.
/// </summary>
internal static class WindowShot
{
    internal const int CommandId = 0x010B;

    internal static void Register(OleMenuCommandService commands) =>
        commands.AddCommand(new OleMenuCommand((_, _) => Save(), new CommandID(AmpleCommands.CommandSet, CommandId)));

    private static void Save()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string path = System.Environment.GetEnvironmentVariable("AMPLE_VS_SHOT")
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ample-vs-shot.png");
        try
        {
            System.Windows.Window? window = System.Windows.Application.Current?.MainWindow;
            if (window is null || window.ActualWidth < 1) throw new System.InvalidOperationException("No main window to draw.");
            int width = (int)window.ActualWidth, height = (int)window.ActualHeight;
            System.Windows.Media.DrawingVisual drawing = new();
            using (System.Windows.Media.DrawingContext context = drawing.RenderOpen())
            {
                context.DrawRectangle(new System.Windows.Media.VisualBrush(window), null, new System.Windows.Rect(0, 0, width, height));
            }

            System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using System.IO.MemoryStream memory = new();
            encoder.Save(memory);
            memory.Position = 0;
            using System.Drawing.Bitmap picture = new(memory);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(picture))
            {
                DrawNativeChildren(new System.Windows.Interop.WindowInteropHelper(window).Handle, graphics, width, height);
            }

            picture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AmpleLog.Write("window picture saved: " + path + " (" + width + "x" + height + ")");
        }
        catch (System.Exception ex)
        {
            AmpleLog.Write("window picture failed: " + ex);
        }
    }

    private delegate bool EnumProc(System.IntPtr window, System.IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumChildWindows(System.IntPtr parent, EnumProc callback, System.IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(System.IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(System.IntPtr window, out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(System.IntPtr window, System.IntPtr context, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(System.IntPtr window, char[] name, int max);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Controls that Visual Studio hosts as their own windows (some debugger tool windows) are not part of the WPF visual tree: ask each of them
    /// for its picture and put it where it is. The windows that only wrap WPF content (HwndWrapper) are skipped.
    /// </summary>
    private static void DrawNativeChildren(System.IntPtr main, System.Drawing.Graphics graphics, int width, int height)
    {
        GetWindowRect(main, out Rect origin);
        EnumChildWindows(main, (child, _) =>
        {
            if (!IsWindowVisible(child) || !GetWindowRect(child, out Rect r)) return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            char[] name = new char[256];
            int length = GetClassName(child, name, 256);
            string className = new(name, 0, System.Math.Max(0, length));
            AmpleLog.Write($"window picture: child {className} {w}x{h} at {r.Left - origin.Left},{r.Top - origin.Top}");
            if (w < 40 || h < 40 || (w >= width * 9 / 10 && h >= height * 9 / 10)) return true;
            if (className.StartsWith("HwndWrapper", System.StringComparison.Ordinal)) return true;
            using System.Drawing.Bitmap part = new(w, h);
            using (System.Drawing.Graphics partGraphics = System.Drawing.Graphics.FromImage(part))
            {
                System.IntPtr context = partGraphics.GetHdc();
                PrintWindow(child, context, 2);
                partGraphics.ReleaseHdc(context);
            }

            graphics.DrawImage(part, r.Left - origin.Left, r.Top - origin.Top);
            return true;
        }, System.IntPtr.Zero);
    }
}
