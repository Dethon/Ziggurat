using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// Several photos sent as one album are one turn carrying all of them, with the album's caption as
// the question. The window is driven through a fake clock rather than by waiting.
public class TelegramBotServiceAlbumTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly TelegramPollingHarness _harness = new();

    [Fact]
    public async Task PhotosSharingAMediaGroupId_ProduceOneNotificationCarryingEveryReference()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            PhotoUpdate(1, messageId: 10, fileId: "one", caption: "/ask compare these", groupId: "g1"),
            PhotoUpdate(2, messageId: 11, fileId: "two", groupId: "g1"),
            PhotoUpdate(3, messageId: 12, fileId: "three", groupId: "g1"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Content.ShouldBe("/ask compare these");
        notification.Attachments.ShouldNotBeNull().Select(a => a.Id)
            .ShouldBe(["jack/one", "jack/two", "jack/three"]);
    }

    // Telegram attaches the caption to whichever item of the album it feels like; the question is
    // the album's, not that one photo's.
    [Fact]
    public async Task TheGroupsCaption_BecomesTheTurnsContentWhereverItArrived()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            PhotoUpdate(1, messageId: 10, fileId: "one", groupId: "g1"),
            PhotoUpdate(2, messageId: 11, fileId: "two", caption: "/ask which is sharper", groupId: "g1"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Content
            .ShouldBe("/ask which is sharper");
    }

    // A straggler on a slow upload must join its album rather than become a second turn with files
    // missing, so every arrival resets the window.
    [Fact]
    public async Task AnItemArrivingWithinTheWindow_JoinsItsGroup()
    {
        await _harness.ReceiveAsync();
        _harness.EnqueueSequence(
            (TimeSpan.Zero, [PhotoUpdate(1, messageId: 10, fileId: "one", caption: "/ask both", groupId: "g1")]),
            (TimeSpan.FromSeconds(1), [PhotoUpdate(2, messageId: 11, fileId: "two", groupId: "g1")]),
            (TimeSpan.FromSeconds(1), [PhotoUpdate(3, messageId: 12, fileId: "three", groupId: "g1")]));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        var notification = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message.ShouldNotBeNull();
        notification.Attachments.ShouldNotBeNull().Count.ShouldBe(3);
    }

    // No ceiling and no early release when the group reaches Telegram's limit: quiet is the only
    // signal that an album is finished.
    [Fact]
    public async Task AGroupWithNoFurtherArrivals_ReleasesAfterTheDebounceAndNotBefore()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(PhotoUpdate(1, messageId: 10, fileId: "one", caption: "/ask look", groupId: "g1"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce - TimeSpan.FromMilliseconds(100));
        (await _harness.ReceiveAsync()).ShouldBeEmpty();

        await _harness.QuietForAsync(TimeSpan.FromMilliseconds(100));
        (await _harness.ReceiveAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AMessageWithNoMediaGroupId_EmitsImmediately()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(PhotoUpdate(1, messageId: 10, fileId: "one", caption: "/ask look"));

        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TwoInterleavedGroups_DoNotMerge()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(
            PhotoUpdate(1, messageId: 10, fileId: "a1", caption: "/ask first pair", groupId: "g1"),
            PhotoUpdate(2, messageId: 20, fileId: "b1", caption: "/ask second pair", groupId: "g2"),
            PhotoUpdate(3, messageId: 11, fileId: "a2", groupId: "g1"),
            PhotoUpdate(4, messageId: 21, fileId: "b2", groupId: "g2"));

        await _harness.RunAsync();
        await _harness.QuietForAsync(Debounce);

        var notifications = (await _harness.ReceiveAsync()).Select(item => item.Message!).ToList();
        notifications.Count.ShouldBe(2);
        notifications.ShouldContain(n =>
            n.Content == "/ask first pair" && n.Attachments!.Count == 2 && n.Attachments![0].Id == "jack/a1");
        notifications.ShouldContain(n =>
            n.Content == "/ask second pair" && n.Attachments!.Count == 2 && n.Attachments![0].Id == "jack/b1");
    }

    public void Dispose() => _harness.Dispose();

    private static Update PhotoUpdate(
        int updateId, int messageId, string fileId, string? caption = null, string? groupId = null)
    {
        var message = TelegramPollingHarness.MediaMessage(
            messageId: messageId, caption: caption, mediaGroupId: groupId);
        message.Photo = TelegramPollingHarness.Photo(fileId);
        return new Update { Id = updateId, Message = message };
    }
}