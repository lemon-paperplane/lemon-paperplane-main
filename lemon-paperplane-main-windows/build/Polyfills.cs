// .NET Framework 4.8 缺少的、新版 C# 语法需要的类型（每个程序集各编一份，internal）。
#nullable enable
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) { ParameterName = parameterName; }
        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class MaybeNullWhenAttribute : Attribute
    {
        public MaybeNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class MaybeNullAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
    internal sealed class AllowNullAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(params string[] members) { Members = members; }
        public string[] Members { get; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class DoesNotReturnAttribute : Attribute { }
}

namespace System
{
    /// <summary>C# 8 索引语法（^1）需要的类型。</summary>
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false) { _value = fromEnd ? ~value : value; }
        public static Index Start => new Index(0);
        public static Index End => new Index(~0, false);
        public static Index FromStart(int value) => new Index(value);
        public static Index FromEnd(int value) => new Index(value, true);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => IsFromEnd ? length - (~_value) : _value;
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index i && Equals(i);
        public override int GetHashCode() => _value;
        public static implicit operator Index(int value) => FromStart(value);
    }

    /// <summary>C# 8 范围语法（a..b）需要的类型。</summary>
    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int s = Start.GetOffset(length), e = End.GetOffset(length);
            return (s, e - s);
        }
        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range r && Equals(r);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}
