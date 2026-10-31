using Domain.DTOs.Household;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// Telegram is the store (ADR 0022): nothing was copied on receipt, so a fetch is a get-file plus a
// download at the moment the agent asks, against the bot the reference names.
public class TelegramFetchAttachmentToolTests
{
    private static readonly byte[] _bytes = [1, 2, 3, 4, 5];

    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly Mock<ITelegramBotClient> _sharedClient = new();
    private readonly Mock<ITelegramBotClient> _otherHouseholdsClient = new();
    private readonly BotRegistry _registry;
    private readonly IServiceProvider _services;

    public TelegramFetchAttachmentToolTests()
    {
        // No chat mapping is registered: a reference names the bot, so a fetch works from a cold
        // start with no chat mapping to consult.
        _registry = new BotRegistry("1000:shared", token => token switch
        {
            "1000:shared" => _sharedClient.Object,
            "1001:jack" => _botClient.Object,
            _ => _otherHouseholdsClient.Object
        });
        _registry.Replace([new HouseholdTelegramBot("household-home", "jack", "1001:jack")]);

        _services = new ServiceCollection()
            .AddSingleton(_registry)
            .AddLogging()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_KnownReference_ReturnsTheBytesBase64()
    {
        GivenTelegramHolds("AgACAgQAAxkBAAIB-_photo", _bytes);

        var result = await FetchAttachmentTool.McpRun(
            "-100200--100200/household-home.jack.AgACAgQAAxkBAAIB-_photo", _services, CancellationToken.None);

        Convert.FromBase64String(result).ShouldBe(_bytes);
    }

    // A file handle is the bot's that saw it: a shared bot's reference is fetched by the shared bot.
    [Fact]
    public async Task McpRun_ASharedBotsReference_IsFetchedByTheSharedBot()
    {
        GivenTelegramHolds("AgACphoto", _bytes, _sharedClient);

        var result = await FetchAttachmentTool.McpRun("100-100/shared.AgACphoto", _services, CancellationToken.None);

        Convert.FromBase64String(result).ShouldBe(_bytes);
    }

    // A reference minted before bots were keyed names the agent whose bot it came through; once two
    // households have a bot for that agent nobody can say whose it was, and it is not guessed.
    [Fact]
    public async Task McpRun_AnAgentsReference_IsNotGuessedOnceTwoHouseholdsHaveItsBot()
    {
        GivenTelegramHolds("AgACphoto", _bytes);
        _registry.Replace(
        [
            new HouseholdTelegramBot("household-home", "jack", "1001:jack"),
            new HouseholdTelegramBot("household-casa", "jack", "2001:casa-jack")
        ]);

        var legacy = await FetchAttachmentTool.McpRun("100-100/jack.AgACphoto", _services, CancellationToken.None);
        var keyed = await FetchAttachmentTool.McpRun("100-100/household-home.jack.AgACphoto", _services, CancellationToken.None);

        legacy.ShouldBeEmpty();
        Convert.FromBase64String(keyed).ShouldBe(_bytes);
    }

    // A reference minted before references named their conversation is still in somebody's history,
    // and still hydrates.
    [Fact]
    public async Task McpRun_AReferenceThatNamesOnlyTheAgent_StillReturnsTheBytes()
    {
        GivenTelegramHolds("AgACphoto", _bytes);

        var result = await FetchAttachmentTool.McpRun("jack/AgACphoto", _services, CancellationToken.None);

        Convert.FromBase64String(result).ShouldBe(_bytes);
    }

    [Fact]
    public async Task McpRun_UnknownAgentInAConversationsReference_AnswersEmpty()
    {
        GivenTelegramHolds("AgACphoto", _bytes);

        var result = await FetchAttachmentTool.McpRun("100-100/stranger.AgACphoto", _services, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpRun_UnknownAgent_AnswersEmptyRatherThanFailing()
    {
        GivenTelegramHolds("AgACphoto", _bytes);

        var result = await FetchAttachmentTool.McpRun("stranger/AgACphoto", _services, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpRun_TelegramCannotAnswer_AnswersEmptyRatherThanFailing()
    {
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<GetFileRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("file is gone"));

        var result = await FetchAttachmentTool.McpRun("jack/AgACphoto", _services, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpRun_ReferenceWithNoAgentSegment_AnswersEmpty()
    {
        GivenTelegramHolds("AgACphoto", _bytes);

        var result = await FetchAttachmentTool.McpRun("AgACphoto", _services, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    private void GivenTelegramHolds(string fileId, byte[] bytes, Mock<ITelegramBotClient>? bot = null)
    {
        var client = bot ?? _botClient;
        client
            .Setup(b => b.SendRequest(
                It.Is<GetFileRequest>(r => r.FileId == fileId), It.IsAny<CancellationToken>()))
            .Returns((GetFileRequest request, CancellationToken _) => Task.FromResult(new TGFile
            {
                FileId = request.FileId,
                FileUniqueId = "u-" + request.FileId,
                FilePath = $"photos/{request.FileId}.jpg",
                FileSize = bytes.Length
            }));

        client
            .Setup(b => b.DownloadFile(
                $"photos/{fileId}.jpg", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Stream destination, CancellationToken ct) =>
                destination.WriteAsync(bytes, ct).AsTask());
    }
}