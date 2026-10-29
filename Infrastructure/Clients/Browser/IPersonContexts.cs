namespace Infrastructure.Clients.Browser;

// A person's browser context as their remembered logins see it: what it holds now, and when it is
// used, so a cookie the site rotates is saved before it is lost.
public interface IPersonContexts
{
    // Raised with the person and the url for every request their context finishes.
    event Action<string, string>? PersonActivity;

    // Raised when every context closed with its connection: whatever lived only in them is gone.
    event Action? ContextsClosed;

    // The context's storage state — cookies, localStorage, IndexedDB — as Playwright writes it, or
    // null when the person has no context on the current connection. IndexedDB can run to tens of
    // megabytes, so this is asked only when a login is kept or forgotten.
    Task<string?> ExportStateAsync(string person, CancellationToken ct = default);

    // The context's cookies alone, in the storage state's shape with no origins, or null with no
    // context: what a save after use and a handoff's before-and-after compare need, at the cost of
    // a cookie list.
    Task<string?> ExportCookiesAsync(string person, CancellationToken ct = default);

    // Replaces the person's context with one built from the given state, closing the old one and
    // every tab in it: how a forgotten login's cookies and storage leave a live context.
    Task ReplaceStateAsync(string person, string storageState, CancellationToken ct = default);
}