using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

// sessionStorage, not localStorage: it lasts as long as the window — across the full loads of a login
// page and back — and a new window, or the installed app opened afresh, starts on the list as it did.
public sealed class OpenTopicMemory(IJSRuntime js) : IOpenTopicMemory
{
    private static string Key(string slug) => $"openTopic:{slug}";

    public async ValueTask<string?> RecallAsync(string slug)
    {
        try
        {
            return await js.InvokeAsync<string?>("sessionStorage.getItem", Key(slug));
        }
        catch (JSException)
        {
            // Storage refused (a private window): nothing to reopen.
            return null;
        }
    }

    public async ValueTask RememberAsync(string slug, string? topicId)
    {
        try
        {
            if (topicId is null)
            {
                await js.InvokeVoidAsync("sessionStorage.removeItem", Key(slug));
            }
            else
            {
                await js.InvokeVoidAsync("sessionStorage.setItem", Key(slug), topicId);
            }
        }
        catch (JSException)
        {
            // Storage refused: the window just will not reopen it.
        }
    }
}