using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Space;

namespace Tests.Unit.WebChat.Client.State;

// Named is what tells an installed app's manifest it may say this space's name and colour: only the
// hub's answer makes a space named, and a path's slug alone never does.
public sealed class SpaceStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly SpaceStore _store;

    public SpaceStoreTests() => _store = new SpaceStore(_dispatcher);

    [Fact]
    public void ASpaceSelectedByItsSlug_IsNotNamedUntilTheHubAnswers()
    {
        _dispatcher.Dispatch(new SelectSpace("kitchen"));

        _store.State.CurrentSlug.ShouldBe("kitchen");
        _store.State.Named.ShouldBeFalse();

        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        _store.State.Named.ShouldBeTrue();
    }

    [Fact]
    public void AnotherSpaceSelected_IsNotNamedWithThePreviousSpacesName()
    {
        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        _dispatcher.Dispatch(new SelectSpace("garage"));

        _store.State.Named.ShouldBeFalse();
    }

    [Fact]
    public void TheSameSpaceSelectedAgain_StaysNamed()
    {
        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        _dispatcher.Dispatch(new SelectSpace("kitchen"));

        _store.State.Named.ShouldBeTrue();
    }

    [Fact]
    public void AnInvalidSpace_IsNotNamed()
    {
        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        _dispatcher.Dispatch(new InvalidSpace());

        _store.State.Named.ShouldBeFalse();
    }

    // What the switcher offers is the hub's last answer, whichever space is open: moving between spaces
    // keeps it, and only the hub's next answer replaces it.
    [Fact]
    public void TheSwitcher_HoldsTheHubsLastAnswer_AcrossSpacesSelectedAndJoined()
    {
        IReadOnlyList<SwitcherEntry> listed =
        [
            new("Main", "#112233", "default", null),
            new("Pantry", "#2a9d8f", "pantry", "https://pantry-a1b2c3.apps.example/"),
            new("Diary", "#445566", null, "https://diary-d1d2d3.apps.example/", Private: true)
        ];

        _dispatcher.Dispatch(new SwitcherLoaded(listed));
        _dispatcher.Dispatch(new SelectSpace("kitchen"));
        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        _store.State.Switcher.ShouldBe(listed);

        _dispatcher.Dispatch(new SwitcherLoaded([listed[0]]));

        _store.State.Switcher.ShouldBe([listed[0]]);
    }

    public void Dispose() => _store.Dispose();
}