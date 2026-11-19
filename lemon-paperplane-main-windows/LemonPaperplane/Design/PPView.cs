using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LemonPaperplane;

/// <summary>
/// 视图基类（一个 SwiftUI View → 一个 PPView 子类）：Build() 返回元素树；订阅的状态变化时合并重建（同一轮消息里的多次变化只重建一次）。
///
///   sealed class RunsBadge : PPView
///   {
///       private readonly RunStore _runs;
///       public RunsBadge(RunStore runs) { _runs = runs; Observe(runs, nameof(RunStore.Active)); }
///       protected override UIElement Build() => UI.HStack(M.Xs, new PPIcon("activity"), UI.Text(_runs.Active.Count.ToString()));
///   }
///
/// - 在构造函数里 Observe(…) / UseState(…)；加载到界面时（Loaded）才真正订阅，卸载（Unloaded）时退订，不可见的视图不重建。
///   卸载期间错过的变化在重新加载时补一次重建。语言切换自动重建。
/// - 重建会换掉整棵子树。为了不打断用户：
///   * 焦点：重建前焦点在本视图里的元素，重建后按 .Key()/.Id() 找回同一个键的元素重新聚焦（文本框还原光标与选区）；
///   * 滚动：本视图里的 ScrollViewer 重建后恢复滚动位置（按 .Key()/.Id()，没有时按出现顺序）；
///   * 正在输入的文本框最好不要被重建：把输入框放进单独的 PPView（不订阅它自己写入的那个属性），
///     或用 Keep("key", () =&gt; new PPTextField(…)) 跨重建复用同一个实例（输入法组字不会被打断以外，其余照常）。
/// </summary>
public abstract class PPView : Decorator
{
    private readonly List<Sub> _subs = new();
    private readonly Dictionary<string, UIElement> _kept = new();
    private readonly HashSet<string> _keptUsed = new();
    private bool _built;
    private bool _dirty;
    private bool _scheduled;
    private bool _loaded;
    private bool _missed;
    private bool _building;

    protected PPView()
    {
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Observe(h => L10n.Shared.Changed += h, h => L10n.Shared.Changed -= h);
    }

    /// <summary>返回本视图的元素树。</summary>
    protected abstract UIElement Build();

    /// <summary>重建次数（调试用）。</summary>
    public int BuildCount { get; private set; }

    /// <summary>请求重建（合并到本轮消息处理完之后、渲染之前）。可以在任何线程调用。</summary>
    public void Invalidate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Invalidate));
            return;
        }
        _dirty = true;
        if (_scheduled || _building) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Flush));
    }

    /// <summary>立即重建（一般不需要，测试用）。</summary>
    public void RebuildNow()
    {
        _dirty = true;
        Flush();
    }

    private void Flush()
    {
        _scheduled = false;
        if (!_dirty) return;
        if (!_loaded && _built)
        {
            _missed = true;
            return;
        }
        DoBuild();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!_built) DoBuild();
        return base.MeasureOverride(constraint);
    }

    private void DoBuild()
    {
        if (_building) return;
        _building = true;
        try
        {
            _dirty = false;
            var focus = _built ? CaptureFocus() : null;
            var scrolls = _built ? CaptureScrolls() : null;
            _keptUsed.Clear();
            var next = Build();
            // 和 Build 之后的 Child 比：Build 里 Keep 可能把复用的元素从本视图上摘了下来（Child 变成 null），这时要重新挂上
            if (!ReferenceEquals(Child, next)) Child = next;
            foreach (var k in _kept.Keys.Where(k => !_keptUsed.Contains(k)).ToList()) _kept.Remove(k);
            _built = true;
            BuildCount++;
            if (focus != null || (scrolls != null && scrolls.Count > 0))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Restore(focus, scrolls)));
        }
        finally
        {
            _building = false;
        }
        if (_dirty) Invalidate(); // Build 里改了状态
    }

    /// <summary>
    /// 跨重建复用同一个元素实例（输入框、正在播放动画的元素）：同一个 key 在上次 Build 里出现过就返回上次的实例（从旧位置摘下），
    /// 否则用 make 新建。update 每次都会调用，用来刷新会变的属性。
    /// </summary>
    protected T Keep<T>(string key, Func<T> make, Action<T>? update = null) where T : UIElement
    {
        _keptUsed.Add(key);
        if (_kept.TryGetValue(key, out var el) && el is T t)
        {
            UI.Detach(t);
            update?.Invoke(t);
            return t;
        }
        var created = make();
        _kept[key] = created;
        update?.Invoke(created);
        return created;
    }

    // MARK: 订阅

    private sealed class Sub
    {
        public Action Attach = () => { };
        public Action Detach = () => { };
        public bool Attached;
    }

    /// <summary>订阅对象的属性变化（不给属性名时任何属性变化都重建）。</summary>
    protected void Observe(INotifyPropertyChanged source, params string[] properties)
    {
        var set = properties.Length == 0 ? null : new HashSet<string>(properties);
        PropertyChangedEventHandler h = (_, e) =>
        {
            if (set == null || string.IsNullOrEmpty(e.PropertyName) || set.Contains(e.PropertyName!)) Invalidate();
        };
        AddSub(() => source.PropertyChanged += h, () => source.PropertyChanged -= h);
    }

    /// <summary>订阅集合变化（ObservableCollection 等）。</summary>
    protected void Observe(INotifyCollectionChanged source)
    {
        NotifyCollectionChangedEventHandler h = (_, _) => Invalidate();
        AddSub(() => source.CollectionChanged += h, () => source.CollectionChanged -= h);
    }

    /// <summary>订阅任意事件：Observe(h =&gt; store.Changed += h, h =&gt; store.Changed -= h)。</summary>
    protected void Observe(Action<EventHandler> add, Action<EventHandler> remove)
    {
        EventHandler h = (_, _) => Invalidate();
        AddSub(() => add(h), () => remove(h));
    }

    /// <summary>主题变化时重建（只有读 Theme.IsDark / Theme.Color 自绘的视图需要；资源引用自动更新）。</summary>
    protected void ObserveTheme() => Observe(h => Theme.Changed += h, h => Theme.Changed -= h);

    /// <summary>局部状态（对应 @State），值变了自动重建。</summary>
    protected State<T> UseState<T>(T initial)
    {
        var s = new State<T>(initial);
        Observe(s);
        return s;
    }

    private void AddSub(Action attach, Action detach)
    {
        var s = new Sub { Attach = attach, Detach = detach };
        _subs.Add(s);
        if (_loaded) { attach(); s.Attached = true; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var s in _subs)
            if (!s.Attached) { s.Attach(); s.Attached = true; }
        OnAppear();
        if (_missed || _dirty)
        {
            _missed = false;
            Invalidate();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        foreach (var s in _subs)
            if (s.Attached) { s.Detach(); s.Attached = false; }
        _missed = true; // 卸载期间收不到通知：重新加载时补一次重建
        OnDisappear();
    }

    /// <summary>显示到界面上（Loaded）。开始轮询、取数据放这里。</summary>
    protected virtual void OnAppear() { }

    /// <summary>从界面上移除（Unloaded）。停止轮询放这里。</summary>
    protected virtual void OnDisappear() { }

    public bool IsAppeared => _loaded;

    // MARK: 焦点与滚动位置

    private sealed class FocusInfo
    {
        public IInputElement? Element;
        public string? Key;
        public int SelStart;
        public int SelLength;
    }

    private FocusInfo? CaptureFocus()
    {
        if (Keyboard.FocusedElement is not DependencyObject f || !IsInside(f)) return null;
        var info = new FocusInfo { Element = f as IInputElement, Key = KeyOf(f) };
        if (f is TextBox tb) { info.SelStart = tb.SelectionStart; info.SelLength = tb.SelectionLength; }
        return info;
    }

    private bool IsInside(DependencyObject d)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, this)) return true;
        return false;
    }

    /// <summary>元素或它最近的带键祖先的键（TextBox 外面包着 PPTextField 时取 PPTextField 的键）。</summary>
    private static string? KeyOf(DependencyObject d)
    {
        int depth = 0;
        for (var p = d; p != null && depth < 6; p = VisualTreeHelper.GetParent(p), depth++)
            if (UI.GetKey(p) is string k) return k + "#" + depth;
        return null;
    }

    private List<(string? Key, double X, double Y)> CaptureScrolls()
    {
        var list = new List<(string?, double, double)>();
        if (Child == null) return list;
        foreach (var sv in Descendants<ScrollViewer>(Child))
            list.Add((UI.GetKey(sv), sv.HorizontalOffset, sv.VerticalOffset));
        return list;
    }

    private void Restore(FocusInfo? focus, List<(string? Key, double X, double Y)>? scrolls)
    {
        if (Child == null) return;
        if (scrolls != null && scrolls.Count > 0)
        {
            var now = Descendants<ScrollViewer>(Child).ToList();
            for (int i = 0; i < now.Count; i++)
            {
                var key = UI.GetKey(now[i]);
                int idx = key != null ? scrolls.FindIndex(s => s.Key == key) : (i < scrolls.Count && scrolls[i].Key == null ? i : -1);
                if (idx < 0) continue;
                now[i].ScrollToHorizontalOffset(scrolls[idx].X);
                now[i].ScrollToVerticalOffset(scrolls[idx].Y);
            }
        }
        if (focus == null) return;
        if (focus.Element is DependencyObject same && IsInside(same) && focus.Element.Focusable)
        {
            Keyboard.Focus(focus.Element);
            return;
        }
        if (focus.Key == null) return;
        foreach (var el in Descendants<UIElement>(Child))
        {
            if (!el.Focusable || KeyOf(el) != focus.Key) continue;
            Keyboard.Focus(el);
            if (el is TextBox tb)
            {
                int st = Math.Min(focus.SelStart, tb.Text.Length);
                tb.Select(st, Math.Min(focus.SelLength, tb.Text.Length - st));
            }
            return;
        }
    }

    /// <summary>深度优先遍历可视树里某种类型的元素。</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }
}

/// <summary>用闭包写的一次性视图：new ViewOf(() =&gt; …, observe: v =&gt; v.Watch(store))。</summary>
public sealed class ViewOf : PPView
{
    private readonly Func<UIElement> _build;

    public ViewOf(Func<UIElement> build, params INotifyPropertyChanged[] observe)
    {
        _build = build;
        foreach (var o in observe) Observe(o);
    }

    protected override UIElement Build() => _build();

    /// <summary>额外订阅一个事件。</summary>
    public ViewOf Watch(Action<EventHandler> add, Action<EventHandler> remove)
    {
        Observe(add, remove);
        return this;
    }
}
