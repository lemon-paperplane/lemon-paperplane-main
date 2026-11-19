using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaperplaneKit;

/// <summary>SVG 解析失败（XML 不合法，或根元素不是 svg）。对应 Mac 的 SVGError。</summary>
public sealed class SvgException : Exception
{
    public SvgException(string message) : base(message) { }
}

/// <summary>
/// 极简 XML 读取器（只为 SVG 子集服务，PaperplaneKit 不引用 System.Xml）：
/// 元素与属性、自闭合、注释、处理指令、DOCTYPE（含内部子集）、CDATA、字符与实体引用（属性值里）。
/// 文本内容全部忽略。结构不合法（标签没闭合、首尾不匹配）时抛 SvgException。
/// </summary>
internal sealed class SvgXmlReader
{
    private readonly string _s;
    private int _pos;

    public SvgXmlReader(string text) { _s = text; }

    public void Read(Action<string, Dictionary<string, string>> start, Action<string> end)
    {
        var open = new Stack<string>();
        bool sawRoot = false;
        while (_pos < _s.Length)
        {
            int lt = _s.IndexOf('<', _pos);
            if (lt < 0) break;
            _pos = lt;
            if (Starts("<!--"))
            {
                int e = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                if (e < 0) throw new SvgException("注释没有结束");
                _pos = e + 3;
            }
            else if (Starts("<![CDATA["))
            {
                int e = _s.IndexOf("]]>", _pos + 9, StringComparison.Ordinal);
                if (e < 0) throw new SvgException("CDATA 没有结束");
                _pos = e + 3;
            }
            else if (Starts("<!"))
            {
                SkipDeclaration();
            }
            else if (Starts("<?"))
            {
                int e = _s.IndexOf("?>", _pos + 2, StringComparison.Ordinal);
                if (e < 0) throw new SvgException("处理指令没有结束");
                _pos = e + 2;
            }
            else if (Starts("</"))
            {
                _pos += 2;
                var name = ReadName();
                SkipSpace();
                if (_pos >= _s.Length || _s[_pos] != '>') throw new SvgException("结束标签不完整：" + name);
                _pos++;
                if (open.Count == 0 || open.Peek() != name) throw new SvgException("结束标签不匹配：" + name);
                open.Pop();
                end(name);
            }
            else
            {
                _pos++;
                var name = ReadName();
                if (name.Length == 0) throw new SvgException("元素名为空");
                if (open.Count == 0 && sawRoot) throw new SvgException("有多个根元素");
                sawRoot = true;
                var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                bool selfClose = false;
                while (true)
                {
                    SkipSpace();
                    if (_pos >= _s.Length) throw new SvgException("标签没有结束：" + name);
                    char c = _s[_pos];
                    if (c == '>') { _pos++; break; }
                    if (c == '/')
                    {
                        if (_pos + 1 < _s.Length && _s[_pos + 1] == '>') { _pos += 2; selfClose = true; break; }
                        throw new SvgException("标签格式错误：" + name);
                    }
                    var an = ReadName();
                    if (an.Length == 0) throw new SvgException("属性名格式错误：" + name);
                    SkipSpace();
                    if (_pos >= _s.Length || _s[_pos] != '=') throw new SvgException("属性缺少值：" + an);
                    _pos++;
                    SkipSpace();
                    if (_pos >= _s.Length || (_s[_pos] != '"' && _s[_pos] != '\'')) throw new SvgException("属性值缺少引号：" + an);
                    char q = _s[_pos++];
                    int ve = _s.IndexOf(q, _pos);
                    if (ve < 0) throw new SvgException("属性值没有结束：" + an);
                    attrs[an] = Decode(_s.Substring(_pos, ve - _pos));
                    _pos = ve + 1;
                }
                start(name, attrs);
                if (selfClose) end(name);
                else open.Push(name);
            }
        }
        if (open.Count > 0) throw new SvgException("元素没有闭合：" + open.Peek());
        if (!sawRoot) throw new SvgException("没有根元素");
    }

    private bool Starts(string p) => string.CompareOrdinal(_s, _pos, p, 0, p.Length) == 0;

    private void SkipSpace()
    {
        while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
    }

    private string ReadName()
    {
        int st = _pos;
        while (_pos < _s.Length)
        {
            char c = _s[_pos];
            if (char.IsWhiteSpace(c) || c == '>' || c == '/' || c == '=' || c == '<' || c == '"' || c == '\'') break;
            _pos++;
        }
        return _s.Substring(st, _pos - st);
    }

    /// <summary>跳过 &lt;!DOCTYPE …&gt;（可能带 [内部子集]）。</summary>
    private void SkipDeclaration()
    {
        int depth = 0;
        _pos += 2;
        while (_pos < _s.Length)
        {
            char c = _s[_pos++];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '>' && depth <= 0) return;
        }
        throw new SvgException("声明没有结束");
    }

    internal static string Decode(string v)
    {
        if (v.IndexOf('&') < 0) return v;
        var sb = new StringBuilder(v.Length);
        int i = 0;
        while (i < v.Length)
        {
            char c = v[i];
            if (c != '&') { sb.Append(c); i++; continue; }
            int semi = v.IndexOf(';', i + 1);
            if (semi < 0) { sb.Append(c); i++; continue; }
            var ent = v.Substring(i + 1, semi - i - 1);
            string? rep = ent switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                _ => null,
            };
            if (rep == null && ent.StartsWith("#", StringComparison.Ordinal))
            {
                bool hex = ent.Length > 1 && (ent[1] == 'x' || ent[1] == 'X');
                var digits = hex ? ent.Substring(2) : ent.Substring(1);
                if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var cp) && cp > 0 && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF))
                    rep = char.ConvertFromUtf32(cp);
            }
            if (rep == null) { sb.Append(c); i++; continue; }
            sb.Append(rep);
            i = semi + 1;
        }
        return sb.ToString();
    }
}
