using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IAccountService
{
    // Null when there is no session: the page is then signed out, whatever it was loaded as.
    Task<SignedInPerson?> GetSignedInAsync();

    void SendToSignIn();

    // A page of the accounts service, which the app's service worker passes to the network: for a
    // household no longer let in it is that household's notice.
    void SendToNotice();

    // The person's own page there — passkeys, devices, signing out — which an installed app has no
    // address bar to reach; it leads back here.
    void OpenAccount();
}