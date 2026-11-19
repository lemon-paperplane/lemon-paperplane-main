using System.Collections.Generic;

namespace PaperplaneKit;

/// <summary>集合小工具（.NET Framework 4.8 没有 GetValueOrDefault）。</summary>
public static class KitCollections
{
    /// <summary>对应 Swift 的 dict[key]：没有这个键时给 default（引用类型为 null）。</summary>
    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> d, TKey key) =>
        d.TryGetValue(key, out var v) ? v : default;

    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key) where TKey : notnull =>
        d.TryGetValue(key, out var v) ? v : default;
}
