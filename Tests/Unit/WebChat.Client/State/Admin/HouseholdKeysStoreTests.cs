using Domain.DTOs.Admin;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Admin;

namespace Tests.Unit.WebChat.Client.State.Admin;

// Checking a saved key again is not a save: the page says it is checking that key, shows what the check
// found beside it, and leaves whatever the owner has typed into its form alone.
public sealed class HouseholdKeysStoreTests
{
    private static readonly HouseholdKeys _keys = new(false, new KeyStatus(KeyState.Working, "work", DateTimeOffset.UnixEpoch), null, []);

    [Fact]
    public void ACheck_IsSaidToBeChecking_ThenShowsWhatItFound_WithoutEmptyingTheForm()
    {
        var dispatcher = new Dispatcher();
        using var store = new HouseholdKeysStore(dispatcher);

        dispatcher.Dispatch(new CheckHouseholdKey(KeyForm.Telegram, "jonas"));
        store.State.Checking.ShouldBe(new KeyCheck(KeyForm.Telegram, "jonas"));

        dispatcher.Dispatch(new HouseholdKeyChecked(KeyForm.Telegram, _keys, null));

        store.State.Checking.ShouldBeNull();
        store.State.Keys.ShouldBe(_keys);
        store.State.NoticeFor.ShouldBe(KeyForm.Telegram);
        store.State.Notice.ShouldBe(new AdminNotice(AdminNoticeKind.Saved, "Checked: it works."));
        store.State.SavedCount.ShouldBe(0);
    }

    [Fact]
    public void ACheckThatFoundAProblem_IsAWarning()
    {
        var dispatcher = new Dispatcher();
        using var store = new HouseholdKeysStore(dispatcher);

        dispatcher.Dispatch(new CheckHouseholdKey(KeyForm.OpenRouter, null));
        dispatcher.Dispatch(new HouseholdKeyChecked(KeyForm.OpenRouter, _keys, "OpenRouter refused it: invalid."));

        store.State.Notice.ShouldBe(new AdminNotice(AdminNoticeKind.Warning, "OpenRouter refused it: invalid."));
    }
}