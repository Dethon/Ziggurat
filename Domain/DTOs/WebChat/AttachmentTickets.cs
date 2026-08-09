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

// What the composer needs to refuse a file as it is picked, rather than after it uploads.
[PublicAPI]
public record AttachmentLimits(
    long MaxBytesPerFile,
    int MaxFilesPerMessage,
    IReadOnlyList<string> AllowedMediaTypes);