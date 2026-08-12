using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Toast;

namespace Tests.Unit.WebChat.Client.State;

public class ToastStoreTests : IDisposable
{
    private const string FallbackMessage = "Something went wrong. Please try again.";

    private readonly Dispatcher _dispatcher;
    private readonly ToastStore _store;

    public ToastStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new ToastStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ShowError_MessageAlreadyOnScreen_LeavesTheListUnchanged()
    {
        _dispatcher.Dispatch(new ShowError("network unreachable"));
        var before = _store.State.Toasts;

        _dispatcher.Dispatch(new ShowError("network unreachable"));

        _store.State.Toasts.ShouldBe(before);
    }

    [Fact]
    public void ShowError_FourthDistinctError_KeepsThreeAndDropsTheOldest()
    {
        _dispatcher.Dispatch(new ShowError("first"));
        _dispatcher.Dispatch(new ShowError("second"));
        _dispatcher.Dispatch(new ShowError("third"));
        _dispatcher.Dispatch(new ShowError("fourth"));

        _store.State.Toasts.Select(t => t.Message).ShouldBe(["second", "third", "fourth"]);
    }

    [Fact]
    public void ShowError_MessageLongerThan150Characters_IsTruncatedWithAnEllipsis()
    {
        var message = new string('x', 151);

        _dispatcher.Dispatch(new ShowError(message));

        _store.State.Toasts.ShouldHaveSingleItem().Message.ShouldBe(new string('x', 150) + "...");
    }

    [Fact]
    public void ShowError_MessageOfExactly150Characters_IsNotTruncated()
    {
        var message = new string('x', 150);

        _dispatcher.Dispatch(new ShowError(message));

        _store.State.Toasts.ShouldHaveSingleItem().Message.ShouldBe(message);
    }

    [Fact]
    public void ShowError_EmptyMessage_IsReplacedByTheFallback()
    {
        _dispatcher.Dispatch(new ShowError(""));

        _store.State.Toasts.ShouldHaveSingleItem().Message.ShouldBe(FallbackMessage);
    }

    [Fact]
    public void ShowError_TwoMessagesDifferingOnlyPast150Characters_DedupeAfterTruncation()
    {
        _dispatcher.Dispatch(new ShowError(new string('x', 151)));
        _dispatcher.Dispatch(new ShowError(new string('x', 200)));

        _store.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public void DismissToast_RemovesThatToastAndLeavesTheOthers()
    {
        _dispatcher.Dispatch(new ShowError("first"));
        _dispatcher.Dispatch(new ShowError("second"));
        var second = _store.State.Toasts.Single(t => t.Message == "second");

        _dispatcher.Dispatch(new DismissToast(second.Id));

        _store.State.Toasts.ShouldHaveSingleItem().Message.ShouldBe("first");
    }

    [Fact]
    public void DismissToast_IdNotPresent_LeavesTheListUnchanged()
    {
        _dispatcher.Dispatch(new ShowError("first"));

        _dispatcher.Dispatch(new DismissToast(Guid.NewGuid()));

        _store.State.Toasts.ShouldHaveSingleItem().Message.ShouldBe("first");
    }
}