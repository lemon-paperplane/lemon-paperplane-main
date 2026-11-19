using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace LemonPaperplane;

/// <summary>
/// 主窗口快捷键（Mac 的 ⌘ → Ctrl、⌥ → Alt）：Shortcuts.Register("Ctrl+R", MainCommands.Run)。
/// 弹出层或对话框打开时不触发。修饰键读 KeyInput.Modifiers（调试桥模拟按键时可覆盖）。
/// </summary>
public static class Shortcuts
{
    private sealed class Entry
    {
        public Key Key;
        public ModifierKeys Mods;
        public Action Action = () => { };
        public Func<bool>? CanRun;
    }

    private static readonly List<Entry> Entries = new();

    /// <summary>注册（同一组合键后注册的覆盖先注册的）。返回菜单里显示用的文字（"Ctrl+R"）。</summary>
    public static string Register(string gesture, Action action, Func<bool>? canRun = null)
    {
        if (!KeyInput.TryParse(gesture, out var key, out var mods)) throw new ArgumentException("快捷键格式不对：" + gesture);
        Entries.RemoveAll(e => e.Key == key && e.Mods == mods);
        Entries.Add(new Entry { Key = key, Mods = mods, Action = action, CanRun = canRun });
        return KeyInput.Format(key, mods);
    }

    public static void Unregister(string gesture)
    {
        if (KeyInput.TryParse(gesture, out var key, out var mods)) Entries.RemoveAll(e => e.Key == key && e.Mods == mods);
    }

    /// <summary>
    /// 返回 true 时这个按键不当快捷键（交给焦点所在的网页）：焦点在终端里时 Ctrl 组合键都给 shell（Ctrl+R 反向搜索等），
    /// 对应 Mac 终端里 Ctrl 键本来就不和 ⌘ 快捷键冲突。由 Features/Editor/WebView2Support.cs 设置。
    /// </summary>
    public static Func<KeyEventArgs, bool>? Bypass { get; set; }

    /// <summary>主窗口 PreviewKeyDown 调用；匹配到就执行并返回 true。</summary>
    public static bool Handle(KeyEventArgs e)
    {
        if (Bypass?.Invoke(e) == true) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = KeyInput.Modifiers;
        var hit = Entries.LastOrDefault(x => x.Key == key && x.Mods == mods);
        if (hit == null || (hit.CanRun != null && !hit.CanRun())) return false;
        hit.Action();
        return true;
    }
}
