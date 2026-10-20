using Shouldly;
using WebChat.Client.Components;

namespace Tests.Unit.WebChat.Client.Components;

// How the desktop rail lays out its agents. Up to three sit side by side with their names beside
// their monograms; from four the names go under the monograms so five fit a row, and past five the
// agents split into even rows so no agent is left alone on a row of its own.
public sealed class AgentRailLayoutTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void UpToThree_SitInOneRow_NamesBeside(int agents, int columns)
    {
        AgentRailLayout.For(agents).ShouldBe(new AgentRailLayout(columns, Stacked: false));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    public void FourAndFive_SitInOneRow_NamesUnder(int agents, int columns)
    {
        AgentRailLayout.For(agents).ShouldBe(new AgentRailLayout(columns, Stacked: true));
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    [InlineData(10, 5)]
    [InlineData(11, 4)]
    public void PastFive_SplitIntoEvenRows(int agents, int columns)
    {
        AgentRailLayout.For(agents).ShouldBe(new AgentRailLayout(columns, Stacked: true));
    }

    [Fact]
    public void NoAgents_StillHaveAColumn()
    {
        AgentRailLayout.For(0).Columns.ShouldBe(1);
    }
}