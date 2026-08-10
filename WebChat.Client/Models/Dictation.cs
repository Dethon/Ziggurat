namespace WebChat.Client.Models;

// Where to post one recording and what lets it in. The browser posts the audio itself, so the
// encoded bytes never enter the WASM heap: .NET hands over the address and the permission, and
// gets back only the words.
public sealed record DictationUpload(string Url, string Token);

// What a recording must obey, learned from the server rather than compiled into the client.
public sealed record DictationLimits(int MaxMs, int MinMs);