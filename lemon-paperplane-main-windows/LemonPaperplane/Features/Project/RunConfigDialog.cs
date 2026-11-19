// 新建 / 编辑 / 复制运行配置（对应 Mac Features/Project/RunConfigDialog.swift）：选模块 → 选类型（runconfig.types）→ 按类型的 Options 渲染表单。
// 名称、表单、环境变量的输入框跨重建复用；标了 live 的下拉选项在对话框打开期间每 4 秒刷新。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>运行配置对话框。</summary>
public sealed class RunConfigDialog : AsyncDialog
{
    private enum Mode { Create, Edit, Duplicate }

    private readonly Project _project;
    private readonly Mode _mode;
    private readonly RunConfig? _source;
    private readonly AppModel _app = AppModel.Shared;

    private readonly State<string> _moduleId;
    private readonly State<List<RunConfigTypeChoice>> _choices;
    private readonly State<string> _typeId;
    private readonly State<bool> _loading;
    private readonly State<bool> _showErrors;
    private readonly State<string> _name;           // 不订阅：输入时不重建
    private Dictionary<string, JToken> _values;
    private List<KeyValuePair<string, string>> _env;
    private Dictionary<string, List<Option>> _liveOptions = new();
    private FormView? _form;
    private string _formKey = "";
    private PPTextField? _nameField;
    private readonly DispatcherTimer _liveTimer;
    private int _loadGen;
    private bool _liveBusy;
    /// <summary>连接远程服务器时运行地址的打开方式（docs/remote.md 5.4）：至少一个。</summary>
    private readonly RemoteLinks _initialLinks;
    private readonly State<bool> _linkForward;
    private readonly State<bool> _linkDirect;

    private RunConfigDialog(string id, Project project, Mode mode, RunConfig? config) : base(id)
    {
        _project = project;
        _mode = mode;
        _source = config;
        _moduleId = UseState(config?.ModuleId ?? project.Modules.FirstOrDefault()?.Id ?? "");
        _choices = UseState(new List<RunConfigTypeChoice>());
        _typeId = UseState(config?.Type ?? "");
        _loading = UseState(false);
        _showErrors = UseState(false);
        _name = new State<string>(config == null ? "" : mode == Mode.Duplicate ? config.Name + " " + L.T("runconfig.copySuffix") : config.Name);
        _values = config == null ? new Dictionary<string, JToken>() : config.Options.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone());
        _env = config == null ? new List<KeyValuePair<string, string>>() : config.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _liveTimer.Tick += (_, _) => RefreshLiveOptions();
        _initialLinks = config != null && project.RemoteLinks.TryGetValue(config.Id, out var l) ? l : new RemoteLinks();
        _linkForward = new State<bool>(_initialLinks.Forward);
        _linkDirect = new State<bool>(_initialLinks.Direct);
    }

    public static void PresentNew(Project project)
    {
        const string id = "runconfig-new";
        Dialogs.Present(id, new RunConfigDialog(id, project, Mode.Create, null));
    }

    public static void PresentEdit(Project project, RunConfig config)
    {
        var id = "runconfig-edit-" + config.Id;
        Dialogs.Present(id, new RunConfigDialog(id, project, Mode.Edit, config));
    }

    public static void PresentDuplicate(Project project, RunConfig config)
    {
        var id = "runconfig-dup-" + config.Id;
        Dialogs.Present(id, new RunConfigDialog(id, project, Mode.Duplicate, config));
    }

    private bool IsEdit => _mode == Mode.Edit;
    private bool IsCreate => _mode == Mode.Create;
    private Module? Module => _project.Module(_moduleId.Value);

    /// <summary>当前类型的表单字段。</summary>
    private List<FormField> Fields
    {
        get
        {
            if (_choices.Value.FirstOrDefault(c => c.Type.Id == _typeId.Value) is { } c) return c.Type.Options;
            return _app.Data.Contributions.RunConfigType(_source?.Plugin, _typeId.Value)?.Options ?? new List<FormField>();
        }
    }

    private Dictionary<string, List<Option>> OptionValues
    {
        get
        {
            var o = Module?.OptionValues ?? new Dictionary<string, List<Option>>();
            if (_choices.Value.FirstOrDefault(c => c.Type.Id == _typeId.Value) is { } c)
                foreach (var kv in c.OptionValues) o[kv.Key] = kv.Value;
            foreach (var kv in _liveOptions) o[kv.Key] = kv.Value;
            return o;
        }
    }

    /// <summary>本类配置里标了 live 的 optionsFrom 键。</summary>
    private List<string> LiveKeys => Fields.Where(f => f.Live && !string.IsNullOrEmpty(f.OptionsFrom)).Select(f => f.OptionsFrom!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    protected override void OnAppear()
    {
        _ = LoadTypes();
        RefreshLiveOptions();
        _liveTimer.Start();
    }

    protected override void OnDisappear() => _liveTimer.Stop();

    private async Task LoadTypes()
    {
        var mid = _moduleId.Value;
        if (mid.Length == 0) return;
        int gen = ++_loadGen;
        _loading.Value = true;
        try
        {
            var list = await _app.Client().RunConfigTypesAsync(_project.Id, mid);
            if (gen != _loadGen) return;
            _choices.Value = list;
            if (IsCreate && list.All(c => c.Type.Id != _typeId.Value)) SetType(list.FirstOrDefault()?.Type.Id ?? "");
        }
        catch (Exception e)
        {
            if (gen == _loadGen && IsCreate) Error.Value = e.Message;
        }
        finally
        {
            if (gen == _loadGen) _loading.Value = false;
        }
    }

    /// <summary>换类型（新建时）：表单值重置为默认值，名称没改过就跟着类型名。</summary>
    private void SetType(string t)
    {
        if (t == _typeId.Value) return;
        _typeId.Value = t;
        if (!IsCreate) return;
        _values = FormView.InitialValues(Fields, new Dictionary<string, JToken>());
        var choices = _choices.Value;
        if (_name.Value.Length == 0 || choices.Any(c => c.Type.Name == _name.Value))
        {
            _name.Value = choices.FirstOrDefault(c => c.Type.Id == t)?.Type.Name ?? _name.Value;
            _nameField?.Sync();
        }
        RefreshLiveOptions();
    }

    /// <summary>实时下拉选项（如已连接设备）：打开时取一次，之后每 4 秒刷新。</summary>
    private async void RefreshLiveOptions()
    {
        var keys = LiveKeys;
        if (keys.Count == 0 || _liveBusy || _app.Api is not { } api) return;
        _liveBusy = true;
        try
        {
            var v = await api.LiveOptionsAsync(_project.Id, _moduleId.Value, keys);
            bool changed = false;
            foreach (var kv in v)
            {
                if (_liveOptions.TryGetValue(kv.Key, out var old) && Json.DeepEquals(old, kv.Value)) continue;
                _liveOptions[kv.Key] = kv.Value;
                changed = true;
            }
            if (changed && _form != null) _form.OptionValues = OptionValues;
        }
        catch { /* 取不到就用分析时的快照 */ }
        finally { _liveBusy = false; }
    }

    private void Close() => Dialogs.Dismiss(DialogId);

    private void Submit()
    {
        if (Busy.Value) return;
        var n = _name.Value.Trim();
        var fields = Fields;
        if (n.Length == 0 || FormView.MissingRequired(fields, _values).Count > 0)
        {
            _showErrors.Value = true;
            if (_form != null) _form.ShowErrors = true;
            if (_nameField != null) _nameField.Invalid = n.Length == 0;
            return;
        }
        var env = new Dictionary<string, string>();
        foreach (var kv in _env) if (kv.Key.Length > 0) env[kv.Key] = kv.Value;
        var opts = FormView.Normalized(fields, _values);
        var mode = _mode;
        var source = _source;
        var mid = _moduleId.Value;
        var type = _typeId.Value;
        var pid = _project.Id;
        var remote = _app.IsRemote;
        var forward = _linkForward.Value;
        var direct = _linkDirect.Value;
        var linksChanged = forward != (mode == Mode.Edit ? _initialLinks.Forward : true) || direct != (mode == Mode.Edit ? _initialLinks.Direct : true);
        Run(async () =>
        {
            var api = _app.Client();
            string configId;
            if (mode == Mode.Edit && source != null)
            {
                await api.UpdateRunConfigAsync(pid, source.Id, n, opts, env);
                configId = source.Id;
            }
            else
            {
                var c = await api.CreateRunConfigAsync(pid, mid, type, n, opts, env);
                _app.Nav.SelectConfig(pid, c.Id);
                configId = c.Id;
            }
            if (remote && linksChanged) await api.SetRemoteLinksAsync(pid, configId, forward, direct);
            _app.Bus.RaiseProjectChanged(pid);
        });
    }

    private static Stack Row(string title, UIElement control)
    {
        var row = HStack(M.Md, Text(title).W(M.FormLabelWidth).Shrink(false), control is FrameworkElement fe ? fe.Flex() : control);
        return row;
    }

    protected override UIElement Build()
    {
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        if (_project.Modules.Count > 1)
        {
            var dd = new PPDropdown<string>(_project.Modules.Select(m => new DropdownOption<string>(m.Id, m.Name, detail: m.RelDir.Length == 0 ? null : m.RelDir)),
                Bind.Of(() => _moduleId.Value, v =>
                {
                    if (v == _moduleId.Value) return;
                    _moduleId.Value = v;
                    _liveOptions.Clear();
                    _ = LoadTypes();
                }), fullWidth: true).Id("runconfig.module");
            if (IsEdit) dd.IsEnabled = false;
            col.Children.Add(Row(L.T("project.module"), dd));
        }
        UIElement typeControl;
        if (IsCreate)
        {
            if (_loading.Value && _choices.Value.Count == 0) typeControl = new PPSpinner(M.IconSmall).Left();
            else
                typeControl = new PPDropdown<string>(_choices.Value.Select(c => new DropdownOption<string>(c.Type.Id, c.Type.Name, iconRef: IconKey.Parse(c.Type.Icon, c.Type.Plugin))),
                    Bind.Of(() => _typeId.Value, SetType), L.T("form.choose"), fullWidth: true).Id("runconfig.type");
        }
        else
        {
            typeControl = Text(_app.Data.Contributions.RunConfigType(null, _typeId.Value)?.Name ?? _typeId.Value);
        }
        col.Children.Add(Row(L.T("runconfig.type"), typeControl));
        var nameField = Keep("name", () => new PPTextField("", _name).Id("runconfig.name"), tf => tf.Invalid = _showErrors.Value && _name.Value.Trim().Length == 0);
        _nameField = nameField;
        col.Children.Add(Row(L.T("common.name"), nameField));

        var fields = Fields;
        if (fields.Count > 0)
        {
            col.Children.Add(new PPDivider(subtle: true));
            var key = _moduleId.Value + "|" + _typeId.Value;
            if (_formKey != key || _form == null)
            {
                _formKey = key;
                _form = new FormView(fields, _values, OptionValues, _showErrors.Value);
            }
            else
            {
                _form.OptionValues = OptionValues;
            }
            Detach(_form);
            col.Children.Add(_form);
        }
        else _form = null;

        col.Children.Add(new PPDivider(subtle: true));
        var envLabel = Text(L.T("config.env")).W(M.FormLabelWidth).Shrink(false).Top();
        envLabel.Margin = new Thickness(0, 6, 0, 0);
        var envEditor = Keep("env", () => new KeyValueEditor(_env, pairs => _env = pairs));
        var envRow = HStack(M.Md, envLabel, envEditor.Flex());
        envRow.Align = CrossAlign.Start;
        col.Children.Add(envRow);
        if (_app.IsRemote)
        {
            // 远程访问方式：端口转发到本机 / 服务器地址（至少留一个）
            var forward = new PPCheckbox(Bind.Of(() => _linkForward.Value, v => { if (v || _linkDirect.Value) _linkForward.Value = v; Invalidate(); }),
                L.T("runconfig.remoteLinks.forward")).Id("runconfig.remoteLinks.forward");
            var direct = new PPCheckbox(Bind.Of(() => _linkDirect.Value, v => { if (v || _linkForward.Value) _linkDirect.Value = v; Invalidate(); }),
                L.T("runconfig.remoteLinks.direct")).Id("runconfig.remoteLinks.direct");
            var label = HStack(M.Xs, Text(L.T("runconfig.remoteLinks")), new PPInfoButton(L.T("runconfig.remoteLinks.info"))).W(M.FormLabelWidth).Shrink(false);
            col.Children.Add(new PPDivider(subtle: true));
            col.Children.Add(HStack(M.Md, label, HStack(M.Lg, forward, direct)));
        }
        if (ErrorAlert is { } alert) col.Children.Add(alert);

        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(IsEdit ? L.T("common.save") : L.T("common.create"), Submit, kind: PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        if (_typeId.Value.Length == 0) ok.IsEnabled = false;
        return new PPDialog(IsEdit ? L.T("runconfig.edit") : L.T("runconfig.new"), col, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close)
        {
            DefaultButton = ok,
            CancelButton = cancel,
        };
    }
}
