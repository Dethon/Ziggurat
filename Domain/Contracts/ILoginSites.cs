namespace Domain.Contracts;

// Whether a person holds a login — remembered, or live until the browser restarts — that covers a
// site. Browsing as a person reaches only such sites, so text a page injects cannot point their
// session at one they never handed off. Throws PersonStateUnreadableException when their
// remembered logins exist and cannot be opened.
public interface ILoginSites
{
    Task<bool> HasLoginAsync(string person, string site, CancellationToken ct = default);
}