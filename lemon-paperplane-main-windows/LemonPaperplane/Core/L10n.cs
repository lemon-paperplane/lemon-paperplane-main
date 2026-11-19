using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>
/// 界面语言。文案在 shared/i18n/&lt;locale&gt;.json（三端共用，编进 PaperplaneKit.dll 的嵌入资源），切换即时生效。
/// 对应 Mac 的 L10n.swift。设置值存在 HKCU\Software\LemonPaperplane（启动时连上服务之前就要用）。
/// </summary>
public sealed class L10n
{
    public static readonly L10n Shared = new();

    /// <summary>生效语言：zh-Hans | en</summary>
    public string Locale { get; private set; }

    /// <summary>设置值：system | zh-Hans | en</summary>
    public string Setting { get; private set; }

    public Localizer Localizer { get; }

    /// <summary>生效语言变化（在 UI 线程触发）。PPView 自动订阅并重建；主窗口据此重建内容。</summary>
    public event EventHandler? Changed;

    private const string ValueName = "Locale";

    private L10n()
    {
        var saved = UiPrefs.Get(ValueName) ?? "system";
        Setting = saved;
        Locale = Localizer.Resolve(saved);
        Localizer = Localizer.Embedded(Locale);
    }

    /// <summary>应用设置值（来自服务的 settings.locale，或设置页切换）。persist=false 时不写注册表（调试桥用）。</summary>
    public void Apply(string? setting, bool persist = true)
    {
        var s = string.IsNullOrEmpty(setting) ? "system" : setting!;
        Setting = s;
        if (persist) UiPrefs.Set(ValueName, s);
        var l = Localizer.Resolve(s);
        if (l == Locale) return;
        Localizer.SetLocale(l);
        Locale = l;
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess()) app.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(this, EventArgs.Empty)));
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>系统首选语言对应的界面语言（拉起服务时告诉它）。</summary>
    public string SystemLocale => Localizer.Resolve("system");
}

/// <summary>取文案：L.T("key")、L.T("key", 参数…) 替换 {0}{1}…（对应 Mac 的 tr）。</summary>
public static class L
{
    public static string T(string key) => L10n.Shared.Localizer.T(key);

    public static string T(string key, params object?[] args) =>
        L10n.Shared.Localizer.T(key, args.Select(a => a switch
        {
            null => "",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
            _ => a.ToString() ?? "",
        }).ToArray());

    public static bool Has(string key) => L10n.Shared.Localizer.Has(key);
}

/// <summary>界面自己的小偏好（语言、主题等启动前就要用的值），存在 HKCU\Software\LemonPaperplane\UI。</summary>
public static class UiPrefs
{
    private const string KeyPath = @"Software\LemonPaperplane\UI";

    public static string? Get(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    /// <summary>调试桥（自动化测试）运行时不写注册表，测试不影响下次正常启动。</summary>
    private static readonly bool ReadOnly = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPERPLANE_DEBUG_BRIDGE"));

    public static void Set(string name, string? value)
    {
        if (ReadOnly) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value == null) k?.DeleteValue(name, false);
            else k?.SetValue(name, value, RegistryValueKind.String);
        }
        catch
        {
            // 写不了注册表（受限账户）时只是下次启动不记得，不影响使用
        }
    }

    public static double? GetDouble(string name) =>
        double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public static void SetDouble(string name, double value) =>
        Set(name, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
}
