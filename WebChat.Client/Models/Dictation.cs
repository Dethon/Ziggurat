namespace WebChat.Client.Models;

// Where to post one recording, what lets it in, and what it must obey. The browser posts the audio
// itself, so the encoded bytes never enter the WASM heap: .NET hands over the address, the
// permission and the rules, and gets back only the words.
//
// The rules travel with the ticket because they come from the same live connection: a dictation
// then obeys what the server holds at the moment it starts, and not what the first render — which
// may have happened before the connection was up — was able to learn.
public sealed record DictationUpload(string Url, string Token, int MaxMs, int MinMs);

// What a recording must obey, learned from the server rather than compiled into the client.
public sealed record DictationLimits(int MaxMs, int MinMs);