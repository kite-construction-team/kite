using System.Diagnostics;
using System.Reflection;
using Xunit;

return await InProcessXunitEntryPoint.RunAssemblyTestsAsync(Assembly.GetExecutingAssembly());

public static class InProcessXunitEntryPoint
{
    public static async Task<int> RunAssemblyTestsAsync(Assembly assembly)
    {
        Console.WriteLine($"=== Running xUnit Suite: {assembly.GetName().Name} ===");
        int passed = 0;
        int failed = 0;
        long suiteStart = Stopwatch.GetTimestamp();

        foreach (Type type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            MethodInfo[] testMethods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
                .ToArray();

            if (testMethods.Length == 0)
            {
                continue;
            }

            foreach (MethodInfo method in testMethods)
            {
                string testName = $"{type.Name}.{method.Name}";
                object? instance = null;
                long testStart = Stopwatch.GetTimestamp();

                try
                {
                    if (!method.IsStatic)
                    {
                        instance = Activator.CreateInstance(type);
                    }

                    object? result = method.Invoke(instance, null);
                    if (result is Task task)
                    {
                        await task.ConfigureAwait(false);
                    }
                    else if (result is ValueTask valueTask)
                    {
                        await valueTask.ConfigureAwait(false);
                    }

                    TimeSpan elapsed = Stopwatch.GetElapsedTime(testStart);
                    Console.WriteLine($"  [PASS] {testName} ({elapsed.TotalMilliseconds:F1} ms)");
                    passed++;
                }
                catch (Exception ex)
                {
                    Exception root = ex is TargetInvocationException tie && tie.InnerException is not null
                        ? tie.InnerException
                        : ex;
                    Console.WriteLine($"  [FAIL] {testName}: {root.GetType().Name} - {root.Message}");
                    Console.WriteLine(root.StackTrace);
                    failed++;
                }
                finally
                {
                    if (instance is IAsyncDisposable asyncDisp)
                    {
                        await asyncDisp.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (instance is IDisposable disp)
                    {
                        disp.Dispose();
                    }
                }
            }
        }

        TimeSpan totalElapsed = Stopwatch.GetElapsedTime(suiteStart);
        Console.WriteLine($"=== Summary ({assembly.GetName().Name}): {passed} Passed, {failed} Failed in {totalElapsed.TotalMilliseconds:F0} ms ===");
        Console.WriteLine();
        return failed == 0 ? 0 : 1;
    }
}
