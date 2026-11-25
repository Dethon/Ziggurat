using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Apps;
using McpServerApps.Settings;

namespace McpServerApps.Store;

// The runtime errors an app's pages reported, newest first, one JSON object a line, at most
// Errors:Kept of them: a new one goes first and the oldest past the limit drops. Kept beside the
// repository (BuiltAppStore.ErrorsFile), so a publish never commits them, the preview never serves them
// and they spend nothing of the app's quota. Each app's file is rewritten whole, one report at a time,
// so concurrent reports never interleave.
public sealed class AppErrorLog(BuiltAppStore store, AppsSettings settings)
{
    // Read by the agent as text, so a quote, a tag or an accent is kept as the browser wrote it rather than
    // as a \u escape; the file is never served to a browser.
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _apps = new(StringComparer.Ordinal);

    public async Task RecordAsync(BuiltApp app, AppErrorEntry entry, CancellationToken ct)
    {
        var gate = _apps.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var file = store.ErrorsFile(app);
            var kept = File.Exists(file) ? await File.ReadAllLinesAsync(file, ct) : [];
            var lines = kept.Prepend(JsonSerializer.Serialize(entry, _json)).Take(settings.Errors.Kept);
            var temp = file + ".tmp";
            await File.WriteAllLinesAsync(temp, lines, ct);
            File.Move(temp, file, overwrite: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The app was deleted while its page was still open: nothing to keep the report for, and
            // never a directory made again where it was.
        }
        finally
        {
            gate.Release();
        }
    }
}