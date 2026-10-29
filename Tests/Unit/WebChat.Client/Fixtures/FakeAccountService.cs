using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeAccountService : IAccountService
{
    public SignedInPerson? SignedIn { get; set; }

    public Exception? ThrowOnGetSignedIn { get; set; }

    public int SentToSignIn { get; private set; }

    public Task<SignedInPerson?> GetSignedInAsync() =>
        ThrowOnGetSignedIn is null
            ? Task.FromResult(SignedIn)
            : Task.FromException<SignedInPerson?>(ThrowOnGetSignedIn);

    public void SendToSignIn() => SentToSignIn++;
}