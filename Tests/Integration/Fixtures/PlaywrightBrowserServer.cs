using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tests.Integration.Fixtures;

// A Playwright browser server on this machine, as the deployment's `apps-browser` container runs one
// (`run-server --unsafe`: a browser per connection, launched with the options the connection sends),
// started from the driver the Playwright package ships beside the tests, so its version is the client's.
// It finds its browsers where an in-process launch does. The server stops when its standard input
// closes, which it does when this is disposed or the test host goes away.
public sealed class PlaywrightBrowserServer : IAsyncLifetime
{
    private Process? _process;

    public string Endpoint { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
        _process = Process.Start(new ProcessStartInfo(Path.Combine(driver, "node", Platform(), Node()))
        {
            ArgumentList = { Path.Combine(driver, "package", "cli.js"), "run-server", "--host", "127.0.0.1", "--port", "0", "--unsafe" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("the Playwright driver did not start");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            const string listening = "Listening on ";
            if (line.StartsWith(listening, StringComparison.Ordinal))
            {
                Endpoint = line[listening.Length..].Trim();
                // What it prints after this is nobody's; left unread, a full pipe would stall it.
                _ = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                return;
            }
        }

        throw new InvalidOperationException("the Playwright browser server exited before it listened");
    }

    public async Task DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        _process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private static string Node() => OperatingSystem.IsWindows() ? "node.exe" : "node";

    private static string Platform() =>
        (OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux")
        + (OperatingSystem.IsWindows() ? "_" : "-")
        + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
}