using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LemonPaperplane;

/// <summary>
/// App 图标生成：LemonPaperplane.exe --gen-icon &lt;输出 .ico&gt; [&lt;预览 .png&gt;]
/// 用 SVG 子集渲染器把 shared/branding/app-icon.svg 画成 16–256 各尺寸（见 IcoWriter.AppIconSizes），写成 Resources/app.ico。
/// 在 Windows 上生成后取回提交到仓库（csproj 里图标是有就用）。
/// </summary>
public static class IconGen
{
    public static int Run(string icoPath, string? previewPng)
    {
        if (Native.GetStdHandle(-11) == IntPtr.Zero) Native.AttachConsole(-1);
        try
        {
            var frames = IcoWriter.AppIconSizes.Select(px => IconStore.Shared.AppIconBitmap(px)).ToList();
            var bytes = IcoWriter.Write(frames);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath))!);
            File.WriteAllBytes(icoPath, bytes);
            Console.Out.WriteLine($"{icoPath}: {frames.Count} sizes ({string.Join(", ", IcoWriter.AppIconSizes)}), {bytes.Length} bytes");
            if (previewPng != null) WritePreview(previewPng);
            // 自检：BMP 帧用 System.Drawing 读回（托盘、WinForms 走它），256 的 PNG 帧用 WPF 解码
            using (var ms = new MemoryStream(bytes))
            {
                foreach (var px in IcoWriter.AppIconSizes.Where(s => s < 256))
                {
                    ms.Position = 0;
                    using var ico = new System.Drawing.Icon(ms, px, px);
                    if (ico.Width != px) throw new InvalidDataException($"read back {px}px, got {ico.Width}px");
                }
                ms.Position = 0;
                var dec = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (!dec.Frames.Any(f => f.PixelWidth == 256)) throw new InvalidDataException("missing 256px frame");
                Console.Out.WriteLine("check ok: " + string.Join(", ", dec.Frames.Select(f => f.PixelWidth)));
                using var tray = IconStore.Shared.TrayIcon(16, IconStore.TrayColor);
                Console.Out.WriteLine($"tray icon ok: {tray.Width}x{tray.Height}");
            }
            Console.Out.Flush();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    /// <summary>预览：各尺寸 App 图标与托盘图标（浅 / 深任务栏）排成一行，按 1 倍与原尺寸放大看锯齿。</summary>
    private static void WritePreview(string path)
    {
        var sizes = IcoWriter.AppIconSizes;
        double width = sizes.Sum(s => s + 16) + 16 + 4 * 48;
        double height = 256 + 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)), null, new Rect(0, 0, width, height));
            double x = 16;
            foreach (var s in sizes)
            {
                var bmp = IconStore.Shared.AppIconBitmap(s);
                dc.DrawImage(bmp, new Rect(x, 16, s, s));
                x += s + 16;
            }
            var light = IconStore.Shared.TrayBitmap(16, Theme.Color(ColorToken.TextPrimary, dark: false));
            var dark = IconStore.Shared.TrayBitmap(16, Colors.White);
            dc.DrawRectangle(Brushes.White, null, new Rect(x, 16, 32, 32));
            dc.DrawImage(light, new Rect(x + 8, 24, 16, 16));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(x + 48, 16, 32, 32));
            dc.DrawImage(dark, new Rect(x + 56, 24, 16, 16));
        }
        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
        Console.Out.WriteLine("preview: " + path);
    }
}
