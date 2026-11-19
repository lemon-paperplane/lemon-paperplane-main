using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PaperplaneKit.Tests;

/// <summary>标在测试方法上（公开、无参数、实例或静态方法，可返回 Task）。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

/// <summary>断言失败。</summary>
public sealed class AssertFailed : Exception
{
    public AssertFailed(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool cond, [CallerArgumentExpression(nameof(cond))] string expr = "")
    {
        if (!cond) throw new AssertFailed("期望为真：" + expr);
    }

    public static void False(bool cond, [CallerArgumentExpression(nameof(cond))] string expr = "")
    {
        if (cond) throw new AssertFailed("期望为假：" + expr);
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailed($"{expr}：期望 <{expected}>，实际 <{actual}>");
    }

    public static void NotNull(object? v, [CallerArgumentExpression(nameof(v))] string expr = "")
    {
        if (v == null) throw new AssertFailed("期望非空：" + expr);
    }

    public static void Null(object? v, [CallerArgumentExpression(nameof(v))] string expr = "")
    {
        if (v != null) throw new AssertFailed($"期望为空：{expr}，实际 <{v}>");
    }

    public static void Near(double expected, double actual, double eps = 1e-6, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (Math.Abs(expected - actual) > eps) throw new AssertFailed($"{expr}：期望 ≈{expected}，实际 {actual}");
    }

    public static T Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new AssertFailed($"期望抛出 {typeof(T).Name}，实际 {e.GetType().Name}：{e.Message}"); }
        throw new AssertFailed($"期望抛出 {typeof(T).Name}，没有抛出");
    }

    public static void Fail(string message) => throw new AssertFailed(message);
}

/// <summary>极简测试运行器：PaperplaneKit.Tests.exe [名字过滤]，失败时退出码 1。</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Select(m => (t, m)))
            .Where(x => filter == null || (x.t.Name + "." + x.m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name)
            .ToList();
        int failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (type, method) in tests)
        {
            var name = type.Name + "." + method.Name;
            try
            {
                var target = method.IsStatic ? null : Activator.CreateInstance(type);
                var r = method.Invoke(target, null);
                if (r is System.Threading.Tasks.Task task) task.GetAwaiter().GetResult();
                Console.WriteLine("  ok    " + name);
            }
            catch (Exception e)
            {
                failed++;
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Console.WriteLine("  FAIL  " + name + "\n        " + inner.Message.Replace("\n", "\n        "));
                if (inner is not AssertFailed) Console.WriteLine("        " + inner.StackTrace?.Replace("\n", "\n        "));
            }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed in {sw.ElapsedMilliseconds} ms");
        return failed == 0 ? 0 : 1;
    }
}
