namespace Infrastructure.Clients.Browser;

// The tab a person logs in on. It opens in that person's own browser context but outside every
// browse session's tab pool, so no ref, browse or action of the agent can reach it while the
// person is typing into it; the person reaches it through the live view alone, and what they
// leave behind in the context — cookies, storage — is what a later `asMe` browse inherits.
public interface IHandoffBrowser
{
    // Opens the url in a fresh handoff tab and answers its id.
    Task<string> OpenHandoffAsync(string person, string url, CancellationToken ct = default);

    // False once the tab is closed — by the handoff ending, or by the browser connection dying
    // underneath it.
    bool IsHandoffOpen(string handoffId);

    Task CloseHandoffAsync(string handoffId);

    // Frames of the page on top of the handoff (the tab, or the newest popup it opened) until the
    // returned handle is disposed.
    Task<IAsyncDisposable> StreamHandoffAsync(
        string handoffId, Func<HandoffFrame, Task> onFrame, CancellationToken ct = default);

    // The person's input, dispatched to the page on top.
    Task SendHandoffInputAsync(string handoffId, HandoffInput input, CancellationToken ct = default);

    // Every host a page of the handoff landed on: the sites a login there may have touched.
    IReadOnlyList<string> HandoffVisitedHosts(string handoffId);
}

public record HandoffFrame(byte[] Jpeg, int ViewportWidth, int ViewportHeight);

// Positions are fractions of the frame (0..1), so the view needs no idea of the page's size.
public abstract record HandoffInput
{
    public sealed record MouseMove(double X, double Y) : HandoffInput;

    public sealed record MouseDown(double X, double Y, string Button = "left") : HandoffInput;

    public sealed record MouseUp(double X, double Y, string Button = "left") : HandoffInput;

    public sealed record Wheel(double DeltaX, double DeltaY) : HandoffInput;

    // A DOM `key` name: "a", "Enter", "Backspace", "Shift".
    public sealed record KeyDown(string Key) : HandoffInput;

    public sealed record KeyUp(string Key) : HandoffInput;

    // Text as a phone keyboard or a paste delivers it, inserted whole.
    public sealed record Text(string Value) : HandoffInput;
}