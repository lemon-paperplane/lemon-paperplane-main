using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace LemonPaperplane;

/// <summary>
/// 控件模板（Design/Templates/*.xaml，作为嵌入资源 templates/&lt;文件名&gt;，运行时 XamlReader 读取）与全局资源安装。
/// </summary>
public static class Templates
{
    /// <summary>读一个模板文件为资源字典。</summary>
    public static ResourceDictionary Load(string fileName)
    {
        var asm = typeof(Templates).Assembly;
        using var s = asm.GetManifestResourceStream("templates/" + fileName) ?? throw new FileNotFoundException("缺少嵌入资源 templates/" + fileName);
        return (ResourceDictionary)XamlReader.Load(s);
    }

    /// <summary>
    /// 启动时调用一次：字体资源（font.ui / font.mono）、控件模板、主题字典（Theme.Install）、全局默认值。
    /// </summary>
    public static void Install(Application app)
    {
        app.Resources["font.ui"] = UIFonts.UI;
        app.Resources["font.mono"] = UIFonts.Mono;
        app.Resources.MergedDictionaries.Add(Load("Controls.xaml"));
        Theme.Install(app);
    }

    /// <summary>
    /// 顶层容器（窗口根、弹出层根、对话框层）的默认文字设置：界面字体正文字号、textPrimary、
    /// 小字号下更清晰的 Display 排版、整像素布局。弹出层不继承窗口的属性，所以每个根都要调用。
    /// </summary>
    public static void ApplyRootDefaults(FrameworkElement root)
    {
        Typo.Body.ApplyInherited(root);
        root.Fg(ColorToken.TextPrimary);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Auto);
        root.UseLayoutRounding = true;
        root.SnapsToDevicePixels = true;
    }
}
