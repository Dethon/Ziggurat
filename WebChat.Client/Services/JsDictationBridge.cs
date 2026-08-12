using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Effects;

namespace WebChat.Client.Services;

// Four calls across the boundary and no more. Everything a dictation does per frame — the clock,
// the level meter, the travel of the discard hint — the browser writes to the DOM itself.
public sealed class JsDictationBridge(IJSRuntime js) : IDictationBridge
{
    public Task RegisterAsync(
        ElementReference microphone, DotNetObjectReference<DictationEffect> callbacks, DictationLimits limits) =>
        js.InvokeVoidAsync("dictation.register", microphone, callbacks, limits).AsTask();

    public Task ConfigureAsync(DictationLimits limits) =>
        js.InvokeVoidAsync("dictation.configure", limits).AsTask();

    public Task StopAsync() => js.InvokeVoidAsync("dictation.stop").AsTask();

    public Task DiscardAsync() => js.InvokeVoidAsync("dictation.discard").AsTask();

    public Task DisposeAsync() => js.InvokeVoidAsync("dictation.dispose").AsTask();
}