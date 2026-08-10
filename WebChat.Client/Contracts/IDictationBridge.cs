using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WebChat.Client.Models;
using WebChat.Client.State.Effects;

namespace WebChat.Client.Contracts;

// The browser end of a dictation. The microphone, the encoder, the gesture thresholds and the
// upload all live in JavaScript, which calls back only at decisions — started, latched, discarded,
// transcript, failed — so the encoded audio never enters the WASM heap.
public interface IDictationBridge
{
    Task RegisterAsync(
        ElementReference microphone, DotNetObjectReference<DictationEffect> callbacks, DictationLimits limits);

    // The cap and the floor arrive with the attachment rules, which need a live connection — so
    // they can land after the microphone was registered, and the browser has to be told.
    Task ConfigureAsync(DictationLimits limits);

    // Ends the recording and asks for the words: the latched stop button, and nothing else.
    Task StopAsync();

    // Throws the recording away: the trash button, Escape, and leaving the topic.
    Task DiscardAsync();

    Task DisposeAsync();
}