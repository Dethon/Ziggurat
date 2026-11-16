namespace Dashboard.Client.Contracts;

// Where a page nobody is signed in to goes: opened again, as a full load the proxy sees, which answers
// it with sign-in on the main host and brings it back.
public interface ISignIn
{
    void SendToSignIn();
}