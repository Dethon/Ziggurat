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
    // null when the person has no context on the current connection.
    Task<string?> ExportStateAsync(string person, CancellationToken ct = default);

    // Replaces the person's context with one built from the given state, closing the old one and
    // every tab in it: how a forgotten site's cookies and storage leave a live context.
    Task ReplaceStateAsync(string person, string storageState, CancellationToken ct = default);
}