using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace WebChat.Client.Helpers;

// One dropdown's contract with the outsidePress JS helper: watch the root while the menu is
// open, stop when it closes, and take the callback when a press lands outside. Three components
// used to carry this dance field-for-field; the component keeps only its open flag and calls
// SyncAsync after each render.
public sealed class OutsidePressWatcher(IJSRuntime js, Action closed) : IDisposable
{
    private DotNetObjectReference<OutsidePressWatcher>? _selfRef;
    private bool _watching;

    public async Task SyncAsync(bool open, ElementReference root)
    {
        if (open == _watching)
        {
            return;
        }

        try
        {
            if (open)
            {
                _selfRef ??= DotNetObjectReference.Create(this);
                await js.InvokeVoidAsync("outsidePress.watch", root, _selfRef);
            }
            else
            {
                await js.InvokeVoidAsync("outsidePress.unwatch", root);
            }

            _watching = open;
        }
        catch (JSException) { /* the helper attaches on the next render */ }
    }

    [JSInvokable]
    public void CloseFromOutsidePress()
    {
        _watching = false;
        closed();
    }

    public void Dispose() => _selfRef?.Dispose();
}