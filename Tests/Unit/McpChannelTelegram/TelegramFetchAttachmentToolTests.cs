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
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5];

    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly IServiceProvider _services;

    public TelegramFetchAttachmentToolTests()
    {
        // No chat mapping is registered: a reference names the agent, so a fetch works from a cold
        // start with no chat mapping to consult.
        var botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });

        _services = new ServiceCollection()
            .AddSingleton(botRegistry)
            .AddLogging()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_KnownReference_ReturnsTheBytesBase64()
    {
        GivenTelegramHolds("AgACphoto", Bytes);

        var result = await FetchAttachmentTool.McpRun("jack/AgACphoto", _services, CancellationToken.None);

        Convert.FromBase64String(result).ShouldBe(Bytes);
    }

    [Fact]
    public async Task McpRun_UnknownAgent_AnswersEmptyRatherThanFailing()
    {
        GivenTelegramHolds("AgACphoto", Bytes);

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
        GivenTelegramHolds("AgACphoto", Bytes);

        var result = await FetchAttachmentTool.McpRun("AgACphoto", _services, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    private void GivenTelegramHolds(string fileId, byte[] bytes)
    {
        _botClient
            .Setup(b => b.SendRequest(
                It.Is<GetFileRequest>(r => r.FileId == fileId), It.IsAny<CancellationToken>()))
            .Returns((GetFileRequest request, CancellationToken _) => Task.FromResult(new TGFile
            {
                FileId = request.FileId,
                FileUniqueId = "u-" + request.FileId,
                FilePath = $"photos/{request.FileId}.jpg",
                FileSize = bytes.Length
            }));

        _botClient
            .Setup(b => b.DownloadFile(
                $"photos/{fileId}.jpg", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Stream destination, CancellationToken ct) =>
                destination.WriteAsync(bytes, ct).AsTask());
    }
}