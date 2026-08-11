using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A person hears about a file that could not be sent in the chat, while they are still there.
// Both grounds are properties of one file, so the turn still runs on the caption and the
// survivors.
public class TelegramBotServiceRefusalTests : IDisposable
{
    private const long OverTheLimit = 21L * 1024 * 1024;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly TelegramPollingHarness _harness = new();

    // The size is on the update, so nothing is fetched to discover it.
    [Fact]
    public async Task AFileAboveTheDownloadLimit_IsRefusedWithNoDownloadAttempted()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask read this");
        message.Document = TelegramPollingHarness.Document(
            fileName: "encyclopaedia.pdf", sizeBytes: OverTheLimit);

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var reply = _harness.Sent.ShouldHaveSingleItem();
        reply.Text.ShouldContain("encyclopaedia.pdf");
        reply.Text.ShouldContain("20 MB");
        _harness.BotClient.Verify(
            b => b.SendRequest(It.IsAny<GetFileRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _harness.BotClient.Verify(
            b => b.DownloadFile(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // One bad file in five must not make someone resend the other four.
    [Fact]
    public async Task ARefusedFile_IsDroppedWhileTheTurnRunsOnTheCaptionAndTheSurvivors()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            DocumentUpdate(1, messageId: 10, fileId: "good-1", fileName: "one.pdf",
                caption: "/ask summarise these", groupId: "g1"),
            DocumentUpdate(2, messageId: 11, fileId: "huge", fileName: "huge.pdf",
                sizeBytes: OverTheLimit, groupId: "g1"),
            DocumentUpdate(3, messageId: 12, fileId: "good-2", fileName: "two.pdf", groupId: "g1"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("/ask summarise these");
        notification.Attachments.ShouldNotBeNull().Select(a => a.Id).ShouldBe(["jack/good-1", "jack/good-2"]);
        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("huge.pdf");
    }

    // The refusal has to say which of five photos failed.
    [Fact]
    public async Task ARefusal_QuotesTheMessageItIsAboutAndNamesEveryFileInOneReply()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            DocumentUpdate(1, messageId: 10, fileId: "good-1", fileName: "one.pdf",
                caption: "/ask summarise these", groupId: "g1"),
            DocumentUpdate(2, messageId: 11, fileId: "huge", fileName: "huge.pdf",
                sizeBytes: OverTheLimit, groupId: "g1"),
            DocumentUpdate(3, messageId: 12, fileId: "odd", fileName: "notes.docx",
                mimeType: "application/msword", groupId: "g1"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        var reply = _harness.Sent.ShouldHaveSingleItem();
        reply.Text.ShouldContain("huge.pdf");
        reply.Text.ShouldContain("notes.docx");
        reply.ReplyParameters.ShouldNotBeNull().MessageId.ShouldBe(11);
    }

    [Fact]
    public async Task AMessageWhoseEveryFileIsRefused_StillRunsAsATextTurnWhenItHasACaption()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask can you read this");
        message.Document = TelegramPollingHarness.Document(
            fileName: "sheet.xlsx", mimeType: "application/vnd.ms-excel");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("/ask can you read this");
        notification.Attachments.ShouldBeNull();
        _harness.Sent.ShouldHaveSingleItem();
    }

    // With nothing left to say, the reply is the whole response.
    [Fact]
    public async Task AMessageWhoseEveryFileIsRefusedAndHasNoCaption_RunsNoTurnAtAll()
    {
        var message = TelegramPollingHarness.MediaMessage(threadId: 42);
        message.Document = TelegramPollingHarness.Document(
            fileName: "sheet.xlsx", mimeType: "application/vnd.ms-excel");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("sheet.xlsx");
    }

    // A file nobody addressed to the bot draws no complaint.
    [Fact]
    public async Task AnUnaddressedMessageWithARefusableFile_SaysNothing()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "just sharing");
        message.Document = TelegramPollingHarness.Document(
            fileName: "sheet.xlsx", mimeType: "application/vnd.ms-excel");

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        _harness.Sent.ShouldBeEmpty();
        (await _harness.ReceiveAsync()).ShouldBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    private static Update DocumentUpdate(
        int updateId,
        int messageId,
        string fileId,
        string fileName,
        string? caption = null,
        string? groupId = null,
        string? mimeType = "application/pdf",
        long? sizeBytes = 4096)
    {
        var message = TelegramPollingHarness.MediaMessage(
            messageId: messageId, caption: caption, mediaGroupId: groupId);
        message.Document = TelegramPollingHarness.Document(fileId, fileName, mimeType, sizeBytes);
        return new Update { Id = updateId, Message = message };
    }
}