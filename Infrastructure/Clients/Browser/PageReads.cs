using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// Reads of a page that answer in milliseconds whenever the page is alive, so a bound on them
// tells a hung page from a slow one. page.content() has no timeout of its own, and a page whose
// script holds its thread kept one browse waiting on it for 35 minutes.
internal static class PageReads
{
    // Generous on purpose: too short fails a busy page that would have answered, too long only
    // makes a dead one wait.
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public static async Task<string> ContentAsync(IPage page, TimeProvider? time = null)
    {
        var read = page.ContentAsync();
        try
        {
            return await read.WaitAsync(Deadline, time ?? TimeProvider.System);
        }
        catch (System.TimeoutException)
        {
            // The tab protocol closes the page, which faults the read nobody is waiting on now.
            _ = read.ContinueWith(t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new PageUnresponsiveException(page.Url);
        }
    }
}

// Thrown out of a tab's work when its page stopped answering. The tab protocol closes that tab,
// which ends the calls still pending on it, and answers TabOutcome.Unresponsive.
internal sealed class PageUnresponsiveException(string url)
    : Exception($"The page at {url} stopped responding.")
{
    public string Url { get; } = url;
}