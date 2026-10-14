using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her log as another agent sees it: the /sugary mount, driven over MCP the way the agent's own
// filesystem tools drive it.
public class McpSugaryServerTests
{
    [Fact]
    public async Task TheMount_IsPublishedAtSugary()
    {
        await using var server = await SugaryServer.StartAsync();

        var content = await server.Client.ReadResourceAsync("filesystem://sugary");
        var text = string.Join("", content.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        text.ShouldContain("\"name\":\"sugary\"");
        text.ShouldContain("\"mountPoint\":\"/sugary\"");
    }

    [Fact]
    public async Task AnEmptyLog_HasItsReadmeFoodsAndTwoEmptyDirectories()
    {
        await using var server = await SugaryServer.StartAsync();

        (await server.GlobAsync("**")).ShouldBe(["/README.md", "/foods.csv", "/items/", "/meals/"]);
        (await server.ReadAsync("/foods.csv"))
            .ShouldBe("food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source\n");
    }

    // A reader that takes carbs for total carbohydrate, or the first estimate for the whole meal,
    // draws the wrong line between a meal and the glucose after it.
    [Fact]
    public async Task TheReadme_ExplainsEveryColumnOfEveryTable()
    {
        await using var server = await SugaryServer.StartAsync();

        var readme = await server.ReadAsync("/README.md");

        string[] columns =
        [
            "meal_id", "eaten_at", "carbs_g", "carbs_low_g", "carbs_high_g", "first_estimate_g",
            "first_estimate_low_g", "first_estimate_high_g", "outcome", "items", "notes", "position", "name",
            "portion", "amount", "carbs_per_100", "source", "confidence", "edited", "food_id", "unit",
            "portion_amount", "portion_carbs_g", "barcode"
        ];
        columns.Where(column => !readme.Contains($"`{column}`")).ShouldBeEmpty();
        readme.ShouldContain("fibre excluded");
        readme.ShouldContain("only the items whose `source` is `estimate`");
        readme.ShouldContain("Europe/Madrid");
    }

    // The only way to change her log is through Sugary itself, so the mount offers nothing that
    // writes, moves or deletes.
    [Fact]
    public async Task TheMount_OffersReadingAndNothingThatWrites()
    {
        await using var server = await SugaryServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        tools.Select(tool => tool.Name).Where(name => name.StartsWith("fs_", StringComparison.Ordinal))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_search"], ignoreOrder: true);
    }
}