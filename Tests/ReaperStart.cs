using System.Reflection;
using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Tests.ReaperFirstTestFramework", "Tests")]

namespace Tests;

// Testcontainers starts its reaper (Ryuk) the first time anything starts a container, under a name fixed
// for the process: testcontainers-ryuk-<session>. Once in about sixty runs that first start failed ("Could
// not find resource 'DockerContainer'") and left its container behind under that name, and every later
// start in the run met Docker's Conflict on it: 614 tests failed in a millisecond each. Started here once,
// before any test runs, a failed start has its leftover removed by name and is tried again, so the reaper
// every fixture then shares is one that came up.
internal static class ReaperStart
{
    internal static async Task<bool> RunAsync(
        Func<CancellationToken, Task> start, Func<CancellationToken, Task> removeLeftover, int attempts,
        CancellationToken ct)
    {
        foreach (var _ in Enumerable.Range(0, attempts))
        {
            try
            {
                await start(ct);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                try
                {
                    await removeLeftover(ct);
                }
                catch (Exception)
                {
                    // Nothing was left under the name, or Docker cannot be asked; the next start says which.
                }
            }
        }

        return false;
    }

    // A run with no Docker (the unit tests alone, anywhere) gives up inside the timeout and carries on:
    // the tests that need a container fail as they would have, and nothing else waits on it.
    internal static async Task StartDefaultAsync()
    {
        if (!TestcontainersSettings.ResourceReaperEnabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        if (endpoint is null)
        {
            return;
        }

        await RunAsync(
            ct => ResourceReaper.GetAndStartDefaultAsync(endpoint, NullLogger.Instance, ct: ct),
            async ct =>
            {
                using var client = new DockerClientBuilder().Build();
                await client.Containers.RemoveContainerAsync(
                    $"testcontainers-ryuk-{ResourceReaper.DefaultSessionId:D}",
                    new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct);
            },
            attempts: 3,
            cts.Token);
    }
}

// xUnit v2's one place to run something before every test of the assembly, and only when tests run:
// discovery never reaches it.
public sealed class ReaperFirstTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(
        AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            try
            {
                await ReaperStart.StartDefaultAsync();
            }
            catch (Exception)
            {
                // No Docker to find, say: the run goes ahead, and a test that needed it says so itself.
            }

            base.RunTestCases(testCases, executionMessageSink, executionOptions);
        }
    }
}