using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain;

// Both advisories guard silent failures: a suffix fighting an explicit sort has no documented
// winner, and `order` quietly turns off sticky routing so the prompt cache goes cold every turn.
// Neither shows up in a response, so these tests are the only thing that proves the guards work
// -- after the appsettings migration nothing in the shipped configuration triggers either one.
public class ProviderRoutingAdvisoriesTests
{
    [Theory]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Price)]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Latency)]
    [InlineData("z-ai/glm-5.2:floor", ProviderSort.Throughput)]
    public void For_SuffixDisagreesWithSort_ReturnsOneAdvisory(string model, ProviderSort sort)
    {
        var advisories = ProviderRoutingAdvisories.For(model, new ProviderRouting { Sort = sort });

        advisories.Count.ShouldBe(1);
        advisories[0].ShouldContain(model);
    }

    [Theory]
    [InlineData("z-ai/glm-5.2:nitro", ProviderSort.Throughput)]
    [InlineData("z-ai/glm-5.2:floor", ProviderSort.Price)]
    public void For_SuffixAgreesWithSort_ReturnsNothing(string model, ProviderSort sort)
    {
        ProviderRoutingAdvisories.For(model, new ProviderRouting { Sort = sort }).ShouldBeEmpty();
    }

    [Fact]
    public void For_SuffixWithNonSortFieldsOnly_ReturnsNothing()
    {
        var routing = new ProviderRouting
        {
            Only = ["deepinfra"],
            Ignore = ["chutes"],
            AllowFallbacks = false
        };

        ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", routing).ShouldBeEmpty();
    }

    [Fact]
    public void For_NoSuffixWithSort_ReturnsNothing()
    {
        ProviderRoutingAdvisories
            .For("z-ai/glm-5.2", new ProviderRouting { Sort = ProviderSort.Price })
            .ShouldBeEmpty();
    }

    [Fact]
    public void For_NullRouting_ReturnsNothing()
    {
        ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("z-ai/glm-5.2")]
    public void For_OrderSet_WarnsAboutStickyRouting(string model)
    {
        var advisories = ProviderRoutingAdvisories.For(
            model, new ProviderRouting { Order = ["deepinfra"] });

        advisories.ShouldContain(a => a.Contains("sticky routing"));
    }

    [Fact]
    public void For_EmptyOrder_ReturnsNothing()
    {
        ProviderRoutingAdvisories
            .For("z-ai/glm-5.2", new ProviderRouting { Order = [] })
            .ShouldBeEmpty();
    }

    // Proves the helper does not stop at the first match.
    [Fact]
    public void For_SuffixConflictAndOrder_ReturnsBothAdvisories()
    {
        var routing = new ProviderRouting { Sort = ProviderSort.Price, Order = ["deepinfra"] };

        var advisories = ProviderRoutingAdvisories.For("z-ai/glm-5.2:nitro", routing);

        advisories.Count.ShouldBe(2);
        advisories.ShouldContain(a => a.Contains(":nitro"));
        advisories.ShouldContain(a => a.Contains("sticky routing"));
    }
}