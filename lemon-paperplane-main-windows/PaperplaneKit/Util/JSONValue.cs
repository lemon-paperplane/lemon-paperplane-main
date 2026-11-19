using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

/// <summary>
/// 通用 JSON 值的便捷取值（对应 Mac 的 Util/JSONValue.swift）。C# 里 JSONValue 就是 Newtonsoft 的 JToken，
/// 这里是 JToken 上的扩展方法和几个构造辅助。所有扩展方法都接受 null（缺失的键）。
/// </summary>
public static class JSONValue
{
    // MARK: 构造

    /// <summary>字符串；null 给 JSON null（对应 init(_ s: String?)）。</summary>
    public static JToken Of(string? s) => s == null ? JValue.CreateNull() : new JValue(s);
    public static JToken Of(bool b) => new JValue(b);
    public static JToken Of(int n) => new JValue((long)n);
    public static JToken Of(long n) => new JValue(n);
    public static JToken Of(double n) => new JValue(n);
    public static JToken Of(IEnumerable<string> a) => new JArray(a.Select(s => (object)s).ToArray());
    public static JToken Of(IDictionary<string, string> m)
    {
        var o = new JObject();
        foreach (var kv in m) o[kv.Key] = kv.Value;
        return o;
    }

    /// <summary>JSON null。</summary>
    public static JToken Null => JValue.CreateNull();

    /// <summary>把任意对象转成 JToken（对应 init(encoding:)，用于把类型化参数平铺进调用输入）。</summary>
    public static JToken Encode(object? value) => Json.ToToken(value);

    // MARK: 取值

    /// <summary>解码为类型化值（对应 decode(as:)）。</summary>
    public static T Decode<T>(this JToken? v) => Json.Decode<T>(v);

    /// <summary>对象取键；不是对象或没有这个键时为 null（对应 subscript(key:)）。</summary>
    public static JToken? Get(this JToken? v, string key) => v is JObject o && o.TryGetValue(key, out var x) ? x : null;

    /// <summary>数组取下标；越界或不是数组时为 null（对应 subscript(index:)）。</summary>
    public static JToken? At(this JToken? v, int index) => v is JArray a && index >= 0 && index < a.Count ? a[index] : null;

    /// <summary>是不是 JSON null（缺失的键返回 false，与 Mac 的 v["x"]?.isNull 一致）。</summary>
    public static bool IsNull(this JToken? v) => v != null && (v.Type == JTokenType.Null || v.Type == JTokenType.Undefined);

    private static bool IsNumber(JToken v) => v.Type == JTokenType.Integer || v.Type == JTokenType.Float;

    private static double NumberOf(JToken v) => v.Type == JTokenType.Integer
        ? Convert.ToDouble(((JValue)v).Value, CultureInfo.InvariantCulture)
        : v.Value<double>();

    /// <summary>字符串；数字转文本（整数不带小数点）；布尔转 "true"/"false"。</summary>
    public static string? StringValue(this JToken? v)
    {
        if (v == null) return null;
        switch (v.Type)
        {
            case JTokenType.String: return (string?)((JValue)v).Value;
            case JTokenType.Integer: return Convert.ToString(((JValue)v).Value, CultureInfo.InvariantCulture);
            case JTokenType.Float:
                var n = NumberOf(v);
                if (Math.Round(n) == n && Math.Abs(n) < 9.2e18) return ((long)n).ToString(CultureInfo.InvariantCulture);
                return n.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e");
            case JTokenType.Boolean: return (bool)((JValue)v).Value! ? "true" : "false";
            // 日期类 token 只在 DateParseHandling 不是 None 时出现；按原文本处理
            case JTokenType.Date:
            case JTokenType.Guid:
            case JTokenType.Uri:
            case JTokenType.TimeSpan:
                return v.ToString();
            default: return null;
        }
    }

    public static double? DoubleValue(this JToken? v)
    {
        if (v == null) return null;
        if (IsNumber(v)) return NumberOf(v);
        switch (v.Type)
        {
            case JTokenType.String:
                return double.TryParse((string?)((JValue)v).Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var d) ? d : null;
            case JTokenType.Boolean: return (bool)((JValue)v).Value! ? 1 : 0;
            default: return null;
        }
    }

    /// <summary>整数（小数截断；超出 int 范围的取边界值，需要 64 位用 Int64Value）。</summary>
    public static int? IntValue(this JToken? v)
    {
        var l = v.Int64Value();
        if (l == null) return null;
        return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, l.Value));
    }

    public static long? Int64Value(this JToken? v)
    {
        if (v == null) return null;
        if (v.Type == JTokenType.Integer && ((JValue)v).Value is long l) return l;
        var d = v.DoubleValue();
        if (d == null || double.IsNaN(d.Value)) return null;
        var t = Math.Truncate(d.Value);
        if (t >= long.MaxValue) return long.MaxValue;
        if (t <= long.MinValue) return long.MinValue;
        return (long)t;
    }

    public static bool? BoolValue(this JToken? v)
    {
        if (v == null) return null;
        if (v.Type == JTokenType.Boolean) return (bool)((JValue)v).Value!;
        if (IsNumber(v)) return NumberOf(v) != 0;
        if (v.Type == JTokenType.String)
        {
            var s = (string?)((JValue)v).Value;
            return s == "true" || s == "1";
        }
        return null;
    }

    public static JArray? ArrayValue(this JToken? v) => v as JArray;
    public static JObject? ObjectValue(this JToken? v) => v as JObject;

    /// <summary>字符串数组（multiselect / list 字段）；元素转不成字符串的跳过。</summary>
    public static List<string>? StringArray(this JToken? v) =>
        v is JArray a ? a.Select(x => x.StringValue()).Where(s => s != null).Select(s => s!).ToList() : null;

    /// <summary>字符串字典（keyvalue 字段）；值转不成字符串的跳过。</summary>
    public static Dictionary<string, string>? StringMap(this JToken? v)
    {
        if (v is not JObject o) return null;
        var d = new Dictionary<string, string>();
        foreach (var p in o.Properties())
        {
            var s = p.Value.StringValue();
            if (s != null) d[p.Name] = s;
        }
        return d;
    }

    /// <summary>是否「空」：null、空串、空数组、空对象（缺失的键也算空）。</summary>
    public static bool IsEmptyValue(this JToken? v)
    {
        if (v == null) return true;
        return v.Type switch
        {
            JTokenType.Null or JTokenType.Undefined => true,
            JTokenType.String => string.IsNullOrEmpty((string?)((JValue)v).Value),
            JTokenType.Array => ((JArray)v).Count == 0,
            JTokenType.Object => ((JObject)v).Count == 0,
            _ => false,
        };
    }

    /// <summary>紧凑 JSON 文本；整数值的小数按整数写（3.0 → 3），与 Mac 一致。</summary>
    public static string JsonString(this JToken? v)
    {
        if (v == null) return "null";
        return Normalize(v).ToString(Newtonsoft.Json.Formatting.None);
    }

    private static JToken Normalize(JToken v)
    {
        switch (v)
        {
            case JValue jv when jv.Type == JTokenType.Float:
                var n = NumberOf(jv);
                return Math.Round(n) == n && Math.Abs(n) < 9.007e15 ? new JValue((long)n) : jv;
            case JArray a:
                return new JArray(a.Select(Normalize).ToArray<object>());
            case JObject o:
                var r = new JObject();
                foreach (var p in o.Properties()) r[p.Name] = Normalize(p.Value);
                return r;
            default:
                return v;
        }
    }
}
