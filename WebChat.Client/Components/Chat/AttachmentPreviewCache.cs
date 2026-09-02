using Domain.DTOs.WebChat;

namespace WebChat.Client.Components.Chat;

// A thumbnail's URL is a minted ticket with a lifetime, and the bubble that rendered it lives
// as long as the transcript does — hours in the background on a phone. Holding the ticket with
// its expiry, rather than the URL alone, is what lets a re-render notice the picture would come
// back broken and mint another before the browser asks.
public sealed class AttachmentPreviewCache(TimeProvider clock)
{
    // The browser fetches the image after the render, not during it, and a phone thawing from
    // the background may take seconds to get to it. A ticket this close to its end is treated
    // as already gone.
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, AttachmentDownload> _held = [];
    private readonly HashSet<string> _failedOnce = [];
    private readonly HashSet<string> _givenUp = [];

    public IReadOnlyList<string> Stale(IEnumerable<string> attachmentIds) =>
        attachmentIds.Where(id => !_givenUp.Contains(id) && !IsFresh(id)).ToList();

    public bool TryGetUrl(string attachmentId, out string url)
    {
        if (IsFresh(attachmentId))
        {
            url = _held[attachmentId].Url;
            return true;
        }

        url = "";
        return false;
    }

    public void Hold(string attachmentId, AttachmentDownload download) => _held[attachmentId] = download;

    public void Loaded(string attachmentId) => _failedOnce.Remove(attachmentId);

    // The image failing to load is the only signal a ticket was pruned early. One failure is
    // that; a failure on the ticket minted to replace it is a file that is gone, and minting on
    // would loop for as long as the bubble is on screen. Returns whether another mint is due.
    public bool Failed(string attachmentId)
    {
        _held.Remove(attachmentId);
        if (_failedOnce.Add(attachmentId))
        {
            return true;
        }

        _givenUp.Add(attachmentId);
        return false;
    }

    private bool IsFresh(string attachmentId) =>
        _held.TryGetValue(attachmentId, out var held) && held.ExpiresAt - Margin > clock.GetUtcNow();
}