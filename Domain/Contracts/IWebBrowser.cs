using Domain.DTOs;

namespace Domain.Contracts;

public interface IWebBrowser
{
    Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default);
    Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default);
    Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default);
    Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default);
    Task CloseSessionAsync(string sessionId, CancellationToken ct = default);

    Task<ImageFetchResult> FetchImagesAsync(ImageFetchRequest request, CancellationToken ct = default);
}

public record ImageFetchRequest(string SessionId, IReadOnlyList<string> Refs);

// One picture asked for, and either its bytes or the wall it hit. Every failure names its own,
// because a model told only "unavailable" either retries a permanent failure or abandons a
// retryable one.
public record FetchedImage(
    string Ref,
    string? MediaType,
    byte[]? Bytes,
    ImageFetchStatus Status,
    // What the entry called it. Carried back because it is the name a note left in the picture's
    // place has to use -- the model chose this image by that label, not by its ref.
    string? Label = null);

public enum ImageFetchStatus
{
    Success,

    // The ref parses but names no image on this page -- an element ref, or one the size filter
    // never listed. Distinct from a dead session: the page is alive and simply does not have it.
    NotAnImageRef,

    // The site answered the fetch with a refusal or an error, through the very browser that was
    // just served the page.
    SiteRefused
}

// The two other walls are not states of a fetch, because neither reaches one: a dead session is
// answered by ImageFetchResult.SessionMissing before any ref is looked up, and a ref past the
// per-call cap is named in the envelope rather than attempted. A status nothing can produce is a
// refusal nobody can receive.

public record ImageFetchResult(
    string SessionId,
    IReadOnlyList<FetchedImage> Images,
    bool SessionMissing = false);

public record StructuredData(
    string Type,
    string RawJson);

public record BrowseRequest(
    string SessionId,
    string Url,
    string? Selector = null,
    int MaxLength = 10000,
    int Offset = 0,
    bool UseReadability = false,
    bool ScrollToLoad = false,
    int ScrollSteps = 3);

public record BrowseResult(
    string SessionId,
    string Url,
    BrowseStatus Status,
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<StructuredData>? StructuredData,
    IReadOnlyList<ModalDismissed>? DismissedModals,
    string? ErrorMessage)
{
    // How many pictures the body lists, and how many only paging forward would reach.
    public int ImageCount { get; init; }

    public int ImagesBeyondWindow { get; init; }
}

public record WebPageMetadata(
    string? Description,
    string? Author,
    DateOnly? DatePublished,
    string? SiteName);

public enum BrowseStatus
{
    Success,
    Partial,
    Error,
    SessionNotFound,
    CaptchaRequired
}

public record ModalDismissed(ModalType Type, string Selector, string? ButtonText);

public record SnapshotRequest(
    string SessionId,
    string? Selector = null);

public record SnapshotResult(
    string SessionId,
    string? Url,
    string? Snapshot,
    int RefCount,
    string? ErrorMessage);

public enum WebActionType
{
    Click, Type, Fill, Select, Press, Clear,
    Hover, Focus, Drag, Back
}

public record WebActionRequest(
    string SessionId,
    string? Ref = null,
    WebActionType Action = WebActionType.Click,
    string? Value = null,
    string? EndRef = null,
    bool WaitForNavigation = false,
    bool Force = false);

public enum WebActionStatus
{
    Success, Error, ElementNotFound, SessionNotFound, Timeout
}

public record WebActionResult(
    string SessionId,
    WebActionStatus Status,
    string? Url,
    bool NavigationOccurred,
    string? Snapshot,
    string? DialogMessage,
    string? ErrorMessage);