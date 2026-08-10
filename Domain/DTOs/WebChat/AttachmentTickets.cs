using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// What the browser is handed so it can put bytes on the upload store. Short-lived and scoped to
// one topic: a public hostname must not mean public disk.
[PublicAPI]
public record UploadTicket(string Token, DateTimeOffset ExpiresAt);

// The same idea on the way back out. Downloads are minted when the transcript renders an
// attachment rather than published as a long-lived URL anyone holding it could read.
[PublicAPI]
public record DownloadTicket(string Token, DateTimeOffset ExpiresAt);

// A minted download, as the browser uses it: the path to fetch and when it stops working.
[PublicAPI]
public record AttachmentDownload(string Url, DateTimeOffset ExpiresAt);

// Permission to turn one recording into words. Space-scoped rather than topic-scoped, because a
// dictation produces composer text and must not force a conversation into existence.
[PublicAPI]
public record DictationTicket(string Token, DateTimeOffset ExpiresAt);

// What comes back: the words, and nothing else. There is no reference, because there is nothing
// stored to refer to.
[PublicAPI]
public record DictationTranscript(string Text);

// What the composer needs to refuse a file as it is picked, rather than after it uploads — plus
// the two numbers a dictation obeys, so changing either needs no new JavaScript shipped.
[PublicAPI]
public record AttachmentLimits(
    long MaxBytesPerFile,
    int MaxFilesPerMessage,
    IReadOnlyList<string> AllowedMediaTypes,
    int MaxDictationMs = 120_000,
    int MinDictationMs = 400);