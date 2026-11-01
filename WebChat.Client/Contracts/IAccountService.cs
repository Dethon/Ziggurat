using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IAccountService
{
    // Null when there is no session: the page is then signed out, whatever it was loaded as.
    Task<SignedInPerson?> GetSignedInAsync();

    void SendToSignIn();

    // A full load of this very page, so the proxy's session check says what it is now.
    void Reload();
}