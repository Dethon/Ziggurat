using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeLaunchLink : ILaunchLink
{
    public LaunchTopic? Topic { get; set; }

    public int Spent { get; private set; }

    public void Spend()
    {
        Topic = null;
        Spent++;
    }
}