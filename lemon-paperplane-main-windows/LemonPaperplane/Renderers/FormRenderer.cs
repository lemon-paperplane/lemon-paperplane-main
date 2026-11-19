// 声明式表单渲染（sdk.FormField，对应 Mac Renderers/FormRenderer.swift）：string / text / boolean / number / select / multiselect /
// keyvalue / path / list / code，外加 password（密码框）。info 显示为 i 图标；advanced 字段折叠进「更多」。
// 表单只建一次：值放在调用方给的字典里，输入中不会因为重建被打断；标红、动态选项直接更新对应的行。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>表单：按字段渲染一列「标签 + 控件」行（标签列宽 formLabelWidth）。对应 Mac FormView。</summary>
public sealed class FormView : PPView
{
    private readonly List<FormField> _fields;
    private readonly Dictionary<string, JToken> _values;
    private readonly double _labelWidth;
    private readonly Action? _onChange;
    private readonly List<FormFieldRow> _basic = new();
    private readonly List<FormFieldRow> _advanced = new();
    private Dictionary<string, List<Option>> _optionValues;
    private bool _showErrors;
    private bool _showAdvanced;
    private UIElement? _advancedBox;
    private PPIcon? _chevron;

    /// <param name="values">表单值（就地修改；调用方一般先用 InitialValues 填好默认值）</param>
    /// <param name="onChange">任何字段变化时调用</param>
    public FormView(List<FormField> fields, Dictionary<string, JToken> values, Dictionary<string, List<Option>>? optionValues = null,
                    bool showErrors = false, double labelWidth = 0, Action? onChange = null)
    {
        _fields = fields;
        _values = values;
        _optionValues = optionValues ?? new Dictionary<string, List<Option>>();
        _labelWidth = labelWidth > 0 ? labelWidth : M.FormLabelWidth;
        _onChange = onChange;
        foreach (var f in fields)
        {
            var row = new FormFieldRow(f, values, OptionsFor(f), _labelWidth, Changed);
            (f.Advanced ? _advanced : _basic).Add(row);
        }
        _showErrors = showErrors;
        ApplyErrors();
    }

    public List<FormField> Fields => _fields;

    /// <summary>表单值（就地修改的那个字典）。</summary>
    public Dictionary<string, JToken> Values => _values;

    /// <summary>显示必填项错误（标签与输入框标红）。</summary>
    public bool ShowErrors
    {
        get => _showErrors;
        set { _showErrors = value; ApplyErrors(); }
    }

    /// <summary>动态下拉选项（optionsFrom 查这里；实时选项变化时整体换掉）。</summary>
    public Dictionary<string, List<Option>> OptionValues
    {
        get => _optionValues;
        set
        {
            _optionValues = value ?? new Dictionary<string, List<Option>>();
            foreach (var r in _basic.Concat(_advanced)) r.SetOptions(OptionsFor(r.Field));
        }
    }

    private void Changed()
    {
        if (_showErrors) ApplyErrors();
        _onChange?.Invoke();
    }

    private void ApplyErrors()
    {
        var missing = _showErrors ? new HashSet<string>(MissingRequired(_fields, _values)) : new HashSet<string>();
        foreach (var r in _basic.Concat(_advanced)) r.SetInvalid(missing.Contains(r.Field.Key));
    }

    private List<Option> OptionsFor(FormField f)
    {
        var output = f.Options.ToList();
        if (!string.IsNullOrEmpty(f.OptionsFrom) && _optionValues.TryGetValue(f.OptionsFrom!, out var dyn))
            foreach (var o in dyn)
                if (output.All(x => x.Value != o.Value)) output.Add(o);
        return output;
    }

    protected override UIElement Build()
    {
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        foreach (var r in _basic) { Detach(r); col.Children.Add(r); }
        if (_advanced.Count > 0)
        {
            _chevron = new PPIcon(_showAdvanced ? "chevron-down" : "chevron-right", M.IconSmall);
            var toggle = HStack(M.Xs, _chevron, Text(L.T("form.more"))).Fg(ColorToken.TextSecondary).Bg(ColorToken.Clear).Left();
            toggle.Cursor = System.Windows.Input.Cursors.Hand;
            toggle.OnClick(() =>
            {
                _showAdvanced = !_showAdvanced;
                if (_chevron != null) _chevron.Key = IconKey.Builtin(_showAdvanced ? "chevron-down" : "chevron-right");
                if (_advancedBox != null) _advancedBox.Visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;
            }).A11y(L.T("form.more")).Id("form.more");
            col.Children.Add(toggle);
            var adv = VStack(M.Md);
            adv.Align = CrossAlign.Stretch;
            foreach (var r in _advanced) { Detach(r); adv.Children.Add(r); }
            adv.Visibility = _showAdvanced ? Visibility.Visible : Visibility.Collapsed;
            _advancedBox = adv;
            col.Children.Add(adv);
        }
        return col;
    }

    // MARK: 值处理

    /// <summary>没给值的字段填默认值。</summary>
    public static Dictionary<string, JToken> InitialValues(IEnumerable<FormField> fields, IDictionary<string, JToken>? values)
    {
        var v = values == null ? new Dictionary<string, JToken>() : values.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var f in fields)
            if (!v.ContainsKey(f.Key) && f.Default != null && !f.Default.IsNull())
                v[f.Key] = f.Default.DeepClone();
        return v;
    }

    /// <summary>必填却为空的字段（boolean 不算）。</summary>
    public static List<string> MissingRequired(IEnumerable<FormField> fields, IDictionary<string, JToken> values) =>
        fields.Where(f => f.Required && f.Type != "boolean" && (!values.TryGetValue(f.Key, out var v) || v.IsEmptyValue()))
            .Select(f => f.Key).ToList();

    /// <summary>提交前整理：去掉 null；数字字段的字符串转数字。</summary>
    public static Dictionary<string, JToken> Normalized(IEnumerable<FormField> fields, IDictionary<string, JToken> values)
    {
        var list = fields.ToList();
        var output = new Dictionary<string, JToken>();
        foreach (var kv in values)
        {
            if (kv.Value == null || kv.Value.IsNull()) continue;
            var f = list.FirstOrDefault(x => x.Key == kv.Key);
            if (f != null && f.Type == "number" && kv.Value.Type == JTokenType.String)
            {
                if (double.TryParse(kv.Value.StringValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) output[kv.Key] = JSONValue.Of(d);
                continue;
            }
            output[kv.Key] = kv.Value;
        }
        return output;
    }
}

/// <summary>表单的一行：标签列（标题、必填星号、i）+ 控件。</summary>
internal sealed class FormFieldRow : PPView
{
    public FormField Field { get; }
    private readonly Dictionary<string, JToken> _values;
    private readonly double _labelWidth;
    private readonly Action _changed;
    private List<Option> _options;
    private bool _invalid;
    private TextBlock? _label;
    private PPTextField? _textField;

    public FormFieldRow(FormField field, Dictionary<string, JToken> values, List<Option> options, double labelWidth, Action changed)
    {
        Field = field;
        _values = values;
        _options = options;
        _labelWidth = labelWidth;
        _changed = changed;
    }

    private bool Tall => Field.Type is "text" or "code" or "keyvalue" or "list" or "multiselect";

    public void SetOptions(List<Option> options)
    {
        if (options.Count == _options.Count && options.Zip(_options, (a, b) => a.Value == b.Value && a.Label == b.Label).All(x => x)) return;
        _options = options;
        if (Field.Type is "select" or "multiselect") Invalidate();
    }

    public void SetInvalid(bool invalid)
    {
        _invalid = invalid;
        _label?.Fg(invalid ? ColorToken.Danger : ColorToken.TextPrimary);
        if (_textField != null) _textField.Invalid = invalid;
    }

    private void Set(JToken v)
    {
        _values[Field.Key] = v;
        _changed();
    }

    private JToken? Get() => _values.TryGetValue(Field.Key, out var v) ? v : null;

    private Bind<string> StringBind => Bind.Of(() => Get().StringValue() ?? "", s => Set(JSONValue.Of(s)));
    private Bind<bool> BoolBind => Bind.Of(() => Get().BoolValue() ?? false, b => Set(JSONValue.Of(b)));
    private Bind<double?> NumberBind => Bind.Of(() => Get().DoubleValue(), d => Set(d is double x ? JSONValue.Of(x) : JSONValue.Null));

    private List<string> ArrayValue()
    {
        var v = Get();
        if (v.StringArray() is { } a) return a;
        // 兼容字符串形式（逗号或换行分隔）
        if (v.StringValue() is { Length: > 0 } s) return s.Split(',', '\n').Select(x => x.Trim()).ToList();
        return new List<string>();
    }

    private List<KeyValuePair<string, string>> MapValue() =>
        (Get().StringMap() ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

    protected override UIElement Build()
    {
        var f = Field;
        _textField = null;
        if (f.Type == "boolean")
        {
            _label = null;
            return HStack(M.Md, Gap(_labelWidth).H(1), new PPCheckbox(BoolBind, f.Title).Fg(ColorToken.TextPrimary).Id("form." + f.Key),
                !string.IsNullOrEmpty(f.Info) ? new PPInfoButton(f.Info!) : null, Spacer());
        }
        _label = Text(f.Title, Typo.Body, _invalid ? ColorToken.Danger : ColorToken.TextPrimary).Lines(2);
        var labelRow = HStack(M.Xs, _label,
            f.Required ? Text("*", Typo.Body, ColorToken.Danger).Shrink(false) : null,
            !string.IsNullOrEmpty(f.Info) ? new PPInfoButton(f.Info!) : null);
        var label = Box(labelRow).W(_labelWidth);
        label.Padding = new Thickness(0, Tall ? 6 : 0, 0, 0);
        var control = Control();
        var row = HStack(M.Md, label, control.Flex());
        if (Tall) { row.Align = CrossAlign.Start; label.VerticalAlignment = VerticalAlignment.Top; }
        return row;
    }

    private FrameworkElement Control()
    {
        var f = Field;
        var placeholder = f.Placeholder ?? "";
        switch (f.Type)
        {
            case "text":
                return new PPTextArea(StringBind, placeholder).Id("form." + f.Key);
            case "code":
                // 代码输入框里是 WebView2：跨重建复用同一个实例，不然每次重建都新开一个网页
                return Keep("code", () => new PPCodeInput(StringBind, placeholder, f.Language ?? "shell").Id("form." + f.Key));
            case "number":
                return (_textField = PPNumberField.Make(placeholder, NumberBind)).Id("form." + f.Key);
            case "select":
            {
                // 非必填时首项为「无」；必填时没有空选项，未选显示占位
                var opts = new List<DropdownOption<string>>();
                if (!f.Required) opts.Add(new DropdownOption<string>("", L.T("form.none")));
                opts.AddRange(_options.Select(o => new DropdownOption<string>(o.Value, o.DisplayLabel)));
                return new PPDropdown<string>(opts, StringBind, f.Placeholder ?? L.T("form.choose"), fullWidth: true).Id("form." + f.Key);
            }
            case "multiselect":
                return new MultiSelectEditor(_options, ArrayValue(), items => Set(JSONValue.Of(items))).Id("form." + f.Key);
            case "keyvalue":
                return new KeyValueEditor(MapValue(), pairs =>
                {
                    var o = new JObject();
                    foreach (var kv in pairs) if (kv.Key.Length > 0) o[kv.Key] = kv.Value;
                    Set(o);
                }).Id("form." + f.Key);
            case "list":
                return new StringListEditor(ArrayValue(), items => Set(JSONValue.Of(items)), placeholder).Id("form." + f.Key);
            case "path":
            {
                _textField = new PPTextField(placeholder, StringBind, mono: true, invalid: _invalid).Id("form." + f.Key);
                var tf = _textField;
                var row = HStack(M.Sm, tf.Flex(), new PPButton(L.T("form.browse"), () => ChoosePath(tf)));
                return row;
            }
            default:
                return (_textField = new PPTextField(placeholder, StringBind, secure: f.IsSecret, invalid: _invalid)).Id("form." + f.Key);
        }
    }

    /// <summary>
    /// 选路径。Mac 的面板可以同时选文件或目录；Windows 的系统对话框只能二选一：
    /// 键名像目录（…dir、directory、folder、path、root、home）时选文件夹，否则选文件。
    /// </summary>
    private void ChoosePath(PPTextField tf)
    {
        var key = Field.Key.ToLowerInvariant();
        bool folder = key.EndsWith("dir", StringComparison.Ordinal) || key.Contains("directory") || key.Contains("folder")
                      || key == "path" || key.EndsWith("root", StringComparison.Ordinal) || key.EndsWith("home", StringComparison.Ordinal);
        var current = Get().StringValue();
        void Apply(string chosen)
        {
            if (string.IsNullOrEmpty(chosen)) return;
            Set(JSONValue.Of(chosen));
            tf.Sync();
        }
        if (AppModel.Shared.IsRemote)
        {
            // 连着远程服务器：选服务器上的路径，文件或文件夹都行（同 Mac；fs.browse 遇到文件会打开它所在的文件夹）
            var start = !string.IsNullOrEmpty(current) && ServerFiles.IsAbsolute(current!) ? current : null;
            ServerFiles.ChooseRemoteAny(Field.Title, start, Apply);
            return;
        }
        string? initial = null;
        try
        {
            if (!string.IsNullOrEmpty(current) && Path.IsPathRooted(current))
                initial = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        }
        catch { /* 不是合法路径 */ }
        string? chosen;
        if (folder)
        {
            chosen = ImportFlow.ChooseFolder(Field.Title, initial);
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = false, Title = Field.Title };
            if (initial != null) dlg.InitialDirectory = initial;
            chosen = dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
        }
        if (!string.IsNullOrEmpty(chosen)) Apply(chosen!);
    }
}

/// <summary>多选：复选框列表（超过 6 项滚动）；没有选项时退化为字符串列表。对应 Mac MultiSelectEditor。</summary>
public sealed class MultiSelectEditor : PPView
{
    private readonly List<Option> _options;
    private readonly List<string> _selection;
    private readonly Action<List<string>> _onChange;

    public MultiSelectEditor(List<Option> options, List<string> selection, Action<List<string>> onChange)
    {
        _options = options;
        _selection = selection.ToList();
        _onChange = onChange;
    }

    protected override UIElement Build()
    {
        if (_options.Count == 0)
            return Box(Keep("list", () => new StringListEditor(_selection, items =>
            {
                _selection.Clear();
                _selection.AddRange(items);
                _onChange(_selection.ToList());
            })));
        var col = VStack(M.Xs);
        foreach (var o in _options)
        {
            var value = o.Value;
            col.Children.Add(new PPCheckbox(Bind.Of(() => _selection.Contains(value), on =>
            {
                if (on) { if (!_selection.Contains(value)) _selection.Add(value); }
                else _selection.RemoveAll(x => x == value);
                _onChange(_selection.ToList());
            }), o.DisplayLabel).Fg(ColorToken.TextPrimary));
        }
        var inner = Box(col).Pad(M.Sm);
        UIElement content = inner;
        if (_options.Count > 6)
        {
            var sv = Scroll(inner);
            sv.MaxHeight = 160;
            content = sv;
        }
        return Box(content).Rounded(M.RadiusMD).Bg(ColorToken.BgInput).Outline(ColorToken.BorderStrong);
    }
}

/// <summary>键值表编辑（环境变量等）：每行 KEY + value + 删除，底部「添加变量」。对应 Mac KeyValueEditor。</summary>
public sealed class KeyValueEditor : PPView
{
    private sealed class Row
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Key = "";
        public string Value = "";
    }

    private readonly List<Row> _rows;
    private readonly Action<List<KeyValuePair<string, string>>> _onChange;
    private readonly string _keyPlaceholder;
    private readonly string _valuePlaceholder;

    public KeyValueEditor(IEnumerable<KeyValuePair<string, string>> pairs, Action<List<KeyValuePair<string, string>>> onChange,
                          string keyPlaceholder = "KEY", string valuePlaceholder = "value")
    {
        _rows = pairs.Select(p => new Row { Key = p.Key, Value = p.Value }).ToList();
        _onChange = onChange;
        _keyPlaceholder = keyPlaceholder;
        _valuePlaceholder = valuePlaceholder;
    }

    private void Emit() => _onChange(_rows.Select(r => new KeyValuePair<string, string>(r.Key.Trim(), r.Value)).ToList());

    protected override UIElement Build()
    {
        var col = VStack(M.Xs);
        col.Align = CrossAlign.Stretch;
        foreach (var r in _rows)
        {
            var row = r;
            var k = Keep("k" + row.Id, () => new PPTextField(_keyPlaceholder, Bind.Of(() => row.Key, v => { row.Key = v; Emit(); }), mono: true).Key("kv.k." + row.Id));
            var v = Keep("v" + row.Id, () => new PPTextField(_valuePlaceholder, Bind.Of(() => row.Value, x => { row.Value = x; Emit(); }), mono: true).Key("kv.v." + row.Id));
            var x = new PPIconButton("x", L.T("common.remove"), () => { _rows.Remove(row); Emit(); Invalidate(); }, size: M.IconButtonSmall, iconSize: M.IconSmall);
            // 键列最宽 220，值列占其余
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 220 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(M.Sm) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(M.Sm) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(k.At(0, 0));
            g.Children.Add(v.At(0, 2));
            g.Children.Add(x.VCenter().At(0, 4));
            col.Children.Add(g);
        }
        col.Children.Add(new PPButton(L.T("form.addRow"), () => { _rows.Add(new Row()); Invalidate(); }, icon: "plus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Left().Id("kv.add"));
        return col;
    }
}

/// <summary>字符串列表编辑（list 字段：JVM 参数、程序参数等）：每行输入 + 删除，底部「添加」。对应 Mac StringListEditor。</summary>
public sealed class StringListEditor : PPView
{
    private sealed class Row
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Text = "";
    }

    private readonly List<Row> _rows;
    private readonly Action<List<string>> _onChange;
    private readonly string _placeholder;

    public StringListEditor(IEnumerable<string> items, Action<List<string>> onChange, string placeholder = "")
    {
        _rows = items.Select(t => new Row { Text = t }).ToList();
        _onChange = onChange;
        _placeholder = placeholder;
    }

    private void Emit() => _onChange(_rows.Select(r => r.Text).Where(t => t.Length > 0).ToList());

    protected override UIElement Build()
    {
        var col = VStack(M.Xs);
        col.Align = CrossAlign.Stretch;
        foreach (var r in _rows)
        {
            var row = r;
            var tf = Keep("t" + row.Id, () => new PPTextField(_placeholder, Bind.Of(() => row.Text, v => { row.Text = v; Emit(); }), mono: true).Key("sl." + row.Id));
            var x = new PPIconButton("x", L.T("common.remove"), () => { _rows.Remove(row); Emit(); Invalidate(); }, size: M.IconButtonSmall, iconSize: M.IconSmall);
            col.Children.Add(HStack(M.Sm, tf.Flex(), x));
        }
        col.Children.Add(new PPButton(L.T("form.addItem"), () => { _rows.Add(new Row()); Invalidate(); }, icon: "plus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Left().Id("list.add"));
        return col;
    }
}

public static partial class Dialogs
{
    /// <summary>
    /// 表单对话框（对应 Mac Dialogs.form）：按 sdk.FormField 渲染，提交时回调拿到表单值（去掉 null，数字转数字）。
    /// onSubmit 抛异常时在框里显示错误；id 给了就用它管理（例如 prompt 要能被另一端关掉）。
    /// </summary>
    public static void Form(string title, List<FormField> fields, Func<Dictionary<string, JToken>, Task> onSubmit,
                            string? message = null, Dictionary<string, JToken>? values = null,
                            Dictionary<string, List<Option>>? optionValues = null, string? ok = null, bool danger = false,
                            string? id = null, Action? onCancel = null, double width = 0)
    {
        id ??= Guid.NewGuid().ToString("N");
        var state = FormView.InitialValues(fields, values);
        FormView? form = null;
        UIElement Fields()
        {
            if (fields.Count == 0) return Gap(0);
            form = new FormView(fields, state, optionValues);
            return form;
        }
        bool Validate()
        {
            if (FormView.MissingRequired(fields, state).Count == 0) return true;
            if (form != null) form.ShowErrors = true;
            return false;
        }
        Present(id, new FormDialogShell(id, title, message, Fields, Validate, () => onSubmit(FormView.Normalized(fields, state)),
            ok, danger, width, onCancel), onCancel);
    }
}
