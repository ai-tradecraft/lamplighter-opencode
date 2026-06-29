using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ChatThroughHarness.Api.Tests;

public static class TestAssembly
{
    [ModuleInitializer]
    public static void ConfigureRuntimeRoot()
    {
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            $"chat-through-harness-tests-{Environment.ProcessId}");
        Environment.SetEnvironmentVariable("CHAT_THROUGH_HARNESS_RUNTIME_ROOT", runtimeRoot);
    }
}
