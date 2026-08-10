using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Effects;

namespace Tests.Unit.WebChat.Client.Fixtures;

// The browser, as far as the client is concerned. A dictation test drives the callbacks the real
// JavaScript would invoke and reads back what the client asked the microphone to do.
public sealed class FakeDictationBridge : IDictationBridge
{
    public bool Registered { get; private set; }
    public DictationLimits? Limits { get; private set; }
    public int Stops { get; private set; }
    public int Discards { get; private set; }

    public Task RegisterAsync(
        ElementReference microphone, DotNetObjectReference<DictationEffect> callbacks, DictationLimits limits)
    {
        Registered = true;
        Limits = limits;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        return Task.CompletedTask;
    }

    public Task DiscardAsync()
    {
        Discards++;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}