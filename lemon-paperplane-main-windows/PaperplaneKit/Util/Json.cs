using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace PaperplaneKit;

/// <summary>
/// 与 daemon（Go encoding/json）对接的 JSON 编解码，对应 Mac 的 Util/Coding.swift（JSONCoding + 容错默认值包装器）。
/// <para>规则：</para>
/// <list type="bullet">
/// <item>属性名 camelCase（C# PascalCase → JSON camelCase），字典键原样（环境变量、插件模型等）；序列化忽略 null。</item>
/// <item>DateParseHandling.None：动态 JSON（JToken）里的时间保持字符串；模型里的 DateTimeOffset? 按 Go 的 RFC 3339 解析。</item>
/// <item>宽松解析（等价 Mac 的 DefArray/DefDict/DefString/DefBool/DefInt/DefDouble）：字段缺失或为 null 时保留属性初始值
///   （集合默认空、字符串默认 ""），整数字段接受小数（截断）；字典里某一项为 null 时，值是集合的用空集合，其他类型丢掉这一项。</item>
/// <item>只读的计算属性（没有 setter）不参与序列化。</item>
/// </list>
/// </summary>
public static class Json
{
    public static JsonSerializerSettings Settings { get; } = new JsonSerializerSettings
    {
        ContractResolver = PaperplaneContractResolver.Instance,
        NullValueHandling = NullValueHandling.Ignore,
        DateParseHandling = DateParseHandling.None,
        FloatParseHandling = FloatParseHandling.Double,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Culture = CultureInfo.InvariantCulture,
        MaxDepth = 256,
        Formatting = Formatting.None,
    };

    public static JsonSerializer Serializer { get; } = JsonSerializer.Create(Settings);

    /// <summary>紧凑 JSON 文本。</summary>
    public static string Serialize(object? value) => JsonConvert.SerializeObject(value, Settings);

    /// <summary>按全局规则解码。文本为 JSON null 时按「空值」规则返回（集合为空、JToken 为 null 值），否则抛出。</summary>
    public static T Deserialize<T>(string text)
    {
        using var reader = CreateReader(text);
        if (!reader.Read()) throw new JsonSerializationException("空的 JSON");
        if (reader.TokenType == JsonToken.Null && TryNullValue(typeof(T), out var nv)) return (T)nv!;
        var v = Serializer.Deserialize<T>(reader);
        if (v == null && !TryNullValue(typeof(T), out _)) throw new JsonSerializationException("JSON 为 null");
        return v!;
    }

    /// <summary>解析成 JToken（时间保持字符串）。</summary>
    public static JToken Parse(string text)
    {
        using var reader = CreateReader(text);
        return JToken.ReadFrom(reader);
    }

    /// <summary>把任意对象转成 JToken（对应 Mac 的 JSONValue(encoding:)）。null 转成 JSON null。</summary>
    public static JToken ToToken(object? value)
    {
        if (value == null) return JValue.CreateNull();
        if (value is JToken t) return t;
        return JToken.FromObject(value, Serializer);
    }

    /// <summary>JToken 解码为类型化值（对应 Mac 的 JSONValue.decode(as:)）。null 按「空值」规则处理。</summary>
    public static T Decode<T>(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            if (TryNullValue(typeof(T), out var nv)) return (T)nv!;
            throw new JsonSerializationException($"JSON 为 null，不能解码为 {typeof(T).Name}");
        }
        return token.ToObject<T>(Serializer)!;
    }

    /// <summary>深拷贝（模型是引用类型，需要 Swift 值语义时用；浅拷贝用记录的 with）。</summary>
    public static T Clone<T>(T value) => value == null ? value : Decode<T>(ToToken(value).DeepClone());

    /// <summary>按 JSON 内容比较（模型的记录相等只比较集合引用）。</summary>
    public static bool DeepEquals(object? a, object? b) => JToken.DeepEquals(ToToken(a), ToToken(b));

    /// <summary>
    /// 构造调用输入，丢掉值为 null 的键（对应 Mac 的 jsonObject）。要显式传 JSON null 用 JValue.CreateNull()。
    /// 值可以是 JToken、字符串、数字、布尔、集合或模型对象。
    /// </summary>
    public static JObject Object(params (string Key, object? Value)[] pairs)
    {
        var o = new JObject();
        foreach (var (k, v) in pairs)
            if (v != null) o[k] = ToToken(v);
        return o;
    }

    internal static JsonTextReader CreateReader(string text) => new JsonTextReader(new StringReader(text))
    {
        DateParseHandling = DateParseHandling.None,
        FloatParseHandling = FloatParseHandling.Double,
        MaxDepth = 256,
        Culture = CultureInfo.InvariantCulture,
    };

    /// <summary>
    /// JSON 为 null 时能直接给出的值（对应 Mac 的 NullDecodable）：集合 → 空，JToken → null 值，可空值类型 → null，EmptyResult → 实例。
    /// </summary>
    internal static bool TryNullValue(Type t, out object? value)
    {
        value = null;
        if (t == typeof(JToken) || t == typeof(JValue)) { value = JValue.CreateNull(); return true; }
        if (t == typeof(EmptyResult)) { value = new EmptyResult(); return true; }
        if (Nullable.GetUnderlyingType(t) != null) return true;
        if (t.IsArray) { value = Array.CreateInstance(t.GetElementType()!, 0); return true; }
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(Dictionary<,>) || def == typeof(HashSet<>))
            {
                value = Activator.CreateInstance(t);
                return true;
            }
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                value = Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetGenericArguments()));
                return true;
            }
        }
        return false;
    }

    /// <summary>字典里某一项为 null 时用的空值（对应 Mac 的 EmptyDecodable）：集合 → 空集合，其他 → null（丢掉这一项）。</summary>
    internal static object? EmptyValue(Type t)
    {
        if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(Dictionary<,>) || def == typeof(HashSet<>)) return Activator.CreateInstance(t);
        }
        return null;
    }
}

/// <summary>没有输出的操作（对应 Mac 的 EmptyResult）。</summary>
public sealed record EmptyResult;

/// <summary>Go 的 time.Time：RFC 3339（带纳秒、带时区），零值 0001-01-01T00:00:00Z 视为「没有」。</summary>
public static class GoTime
{
    private static readonly string[] Formats =
    {
        "yyyy'-'MM'-'dd'T'HH':'mm':'ssK",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'FFFFFFFK",
    };

    /// <summary>Go 零值时间（0001-01-01）或 null 视为「没有」。</summary>
    public static bool IsZero(DateTimeOffset? d) => d == null || d.Value.ToUnixTimeSeconds() < -62_000_000_000L;

    /// <summary>解析 RFC 3339。小数秒超过 7 位（DateTimeOffset 精度 100ns）的截掉。解析不了返回 null。</summary>
    public static DateTimeOffset? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var s = raw!;
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < s.Length && s[end] >= '0' && s[end] <= '9') end++;
            var digits = end - dot - 1;
            if (digits == 0) return null;
            if (digits > 7) s = s.Substring(0, dot + 8) + s.Substring(end);
        }
        try
        {
            if (DateTimeOffset.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        }
        catch (ArgumentOutOfRangeException)
        {
            // 0001-01-01 带正时区换算到 UTC 会越界
        }
        return null;
    }

    /// <summary>格式化为 UTC 的 RFC 3339（毫秒）。</summary>
    public static string Format(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>全局解析规则的落点：命名、计算属性、整数/时间/字典的宽松转换。</summary>
internal sealed class PaperplaneContractResolver : DefaultContractResolver
{
    internal static readonly PaperplaneContractResolver Instance = new PaperplaneContractResolver();

    private PaperplaneContractResolver()
    {
        NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: false);
    }

    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var p = base.CreateProperty(member, memberSerialization);
        // 没有 setter 的计算属性（如 Project.AllRunConfigs、RunBrief 的 Id）不序列化
        if (member is PropertyInfo pi && pi.GetSetMethod(false) == null && pi.GetCustomAttribute<JsonPropertyAttribute>() == null)
            p.Ignored = true;
        return p;
    }

    protected override JsonConverter? ResolveContractConverter(Type objectType)
    {
        if (objectType == typeof(int) || objectType == typeof(long)) return LenientIntegerConverter.Instance;
        if (objectType == typeof(DateTimeOffset)) return GoTimeConverter.Instance;
        if (objectType.IsGenericType && objectType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && objectType.GetGenericArguments()[0] == typeof(string))
            return LenientDictionaryConverter.Instance;
        return base.ResolveContractConverter(objectType);
    }
}

/// <summary>整数字段：接受整数、小数（截断，对应 DefInt）、数字字符串、布尔；null 给 0（可空类型给 null）。</summary>
internal sealed class LenientIntegerConverter : JsonConverter
{
    internal static readonly LenientIntegerConverter Instance = new LenientIntegerConverter();
    public override bool CanConvert(Type objectType) => true;
    public override bool CanWrite => false;
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var under = Nullable.GetUnderlyingType(objectType);
        var target = under ?? objectType;
        long v;
        switch (reader.TokenType)
        {
            case JsonToken.Null:
            case JsonToken.Undefined:
                if (under != null) return null;
                v = 0;
                break;
            case JsonToken.Integer:
                v = reader.Value is long l ? l : FromDouble(double.Parse(Convert.ToString(reader.Value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture));
                break;
            case JsonToken.Float:
                v = FromDouble(Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture));
                break;
            case JsonToken.String:
                var s = (string?)reader.Value ?? "";
                if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ls)) v = ls;
                else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) v = FromDouble(ds);
                else v = 0;
                break;
            case JsonToken.Boolean:
                v = (bool)reader.Value! ? 1 : 0;
                break;
            default:
                throw new JsonSerializationException($"{reader.Path}：期望整数，实际 {reader.TokenType}");
        }
        if (target == typeof(int)) return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
        return v;
    }

    private static long FromDouble(double d)
    {
        if (double.IsNaN(d)) return 0;
        d = Math.Truncate(d);
        if (d >= long.MaxValue) return long.MaxValue;
        if (d <= long.MinValue) return long.MinValue;
        return (long)d;
    }
}

/// <summary>DateTimeOffset：按 Go 的 RFC 3339 读写；解析不了时给 null（Mac 会让整个解码失败，这里更宽松）。</summary>
internal sealed class GoTimeConverter : JsonConverter
{
    internal static readonly GoTimeConverter Instance = new GoTimeConverter();
    public override bool CanConvert(Type objectType) => objectType == typeof(DateTimeOffset) || objectType == typeof(DateTimeOffset?);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var nullable = Nullable.GetUnderlyingType(objectType) != null;
        DateTimeOffset? d = reader.TokenType switch
        {
            JsonToken.String => GoTime.Parse((string?)reader.Value),
            JsonToken.Date => reader.Value switch
            {
                DateTimeOffset o => o,
                DateTime t => new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t),
                _ => null,
            },
            JsonToken.Null or JsonToken.Undefined => null,
            _ => throw new JsonSerializationException($"{reader.Path}：期望时间字符串，实际 {reader.TokenType}"),
        };
        if (d == null && !nullable) return default(DateTimeOffset);
        return d;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTimeOffset d) writer.WriteValue(GoTime.Format(d));
        else writer.WriteNull();
    }
}

/// <summary>
/// Dictionary&lt;string, T&gt;（对应 Mac 的 DefDict）：整个字典为 null 给空字典；某一项为 null 时，
/// 值是集合的用空集合（Go 的 map[string][]T 里 nil 切片输出为 null），其他类型丢掉这一项。
/// </summary>
internal sealed class LenientDictionaryConverter : JsonConverter
{
    internal static readonly LenientDictionaryConverter Instance = new LenientDictionaryConverter();
    public override bool CanConvert(Type objectType) => true;
    public override bool CanWrite => false;
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var dict = (IDictionary)Activator.CreateInstance(objectType)!;
        if (reader.TokenType == JsonToken.Null || reader.TokenType == JsonToken.Undefined) return dict;
        if (reader.TokenType != JsonToken.StartObject)
            throw new JsonSerializationException($"{reader.Path}：期望对象，实际 {reader.TokenType}");
        var valueType = objectType.GetGenericArguments()[1];
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonToken.EndObject:
                    return dict;
                case JsonToken.Comment:
                    continue;
                case JsonToken.PropertyName:
                    var key = (string)reader.Value!;
                    if (!reader.Read()) throw new JsonSerializationException("JSON 意外结束");
                    if (reader.TokenType == JsonToken.Null || reader.TokenType == JsonToken.Undefined)
                    {
                        var empty = Json.EmptyValue(valueType);
                        if (empty != null) dict[key] = empty;
                        continue;
                    }
                    dict[key] = serializer.Deserialize(reader, valueType);
                    continue;
                default:
                    throw new JsonSerializationException($"{reader.Path}：意外的 {reader.TokenType}");
            }
        }
        throw new JsonSerializationException("JSON 意外结束");
    }
}
