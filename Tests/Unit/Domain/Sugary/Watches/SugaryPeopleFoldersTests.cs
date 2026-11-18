using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.Tools.Sugary.Vfs;
using Domain.Tools.Sugary.Watches;
using McpServerSugary.Tables;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary.Watches;

// A watch names a person by the folder /sugary/people lists them under; the two places share the rule,
// so they are asked the same question here and must answer alike, twins included.
public class SugaryPeopleFoldersTests
{
    private const string Household = "household-home";

    [Fact]
    public async Task TheFolders_AreTheOnesTheLogListsPeopleUnder()
    {
        var people = new HouseholdSugaryPeople([
            new SugaryPerson("user-dethon", "Dethon", true),
            new SugaryPerson("user-ana", "Ana María/López", false),
            new SugaryPerson("user-twin", "ana maría lópez", false),
            new SugaryPerson("user-dani", "Dani", false)
        ]);
        var household = new Mock<ICallHousehold>();
        household.SetupGet(h => h.Id).Returns(Household);
        household.Setup(h => h.SettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdSettings { Name = "Casa", TimeZone = "UTC" });
        household.Setup(h => h.SugaryPeopleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(people);
        var log = new SugaryFileSystem(new InMemorySugaryLog(), TimeZoneInfo.Utc, new DuckDbTableEngine())
            .For(new FileSystemCaller(
                new ConversationContext("jonas", "c", "user-dethon", new ReplyTarget("signalr", "c"),
                    Principal: new Principal("signalr", "user-dethon", new Account(Household, "user-dethon"))),
                null, Household: household.Object));

        var listed = (await log.GlobAsync("/people", "*/", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries
            .Select(e => e["/people/".Length..].TrimEnd('/'));

        SugaryPeopleFolders.Of(people.People, "user-dethon").Select(o => o.Folder).ShouldBe(listed, ignoreOrder: true);
        SugaryPeopleFolders.Of(people.People, "user-dethon").Single(o => o.Person.UserId == "user-twin").Folder
            .ShouldBe("ana-maría-lópez-2");
    }
}