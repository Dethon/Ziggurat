using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

// Which of the keys page's forms an outcome belongs to, so it is said beside the key it was about.
public enum KeyForm
{
    OpenRouter,
    Idealista,
    Telegram,
    Lemonade
}

public record LoadHouseholdKeys : IAction;

public record HouseholdKeysLoaded(HouseholdKeys Keys) : IAction;

public record SaveOpenRouterKey(OpenRouterKey Key) : IAction;

public record SaveIdealistaKeys(IdealistaKeys Keys) : IAction;

public record SaveTelegramBot(string AgentId, TelegramBotKey Bot) : IAction;

public record SaveLemonadeBox(LemonadeBox Box) : IAction;

public record RemoveLemonadeBox : IAction;

public record HouseholdKeysSaved(KeyForm Form, HouseholdKeys Keys, string? Warning) : IAction;

// Which saved key is being put to its provider again: a form's, and for a bot the agent it answers as.
public sealed record KeyCheck(KeyForm Form, string? AgentId);

public record CheckHouseholdKey(KeyForm Form, string? AgentId) : IAction;

// What a check found, said beside its form; nothing typed into the form is touched, since nothing was saved.
public record HouseholdKeyChecked(KeyForm Form, HouseholdKeys Keys, string? Warning) : IAction;

// A load or a save the admin API did not answer with the keys: refused (a key its provider rejected
// among them), not allowed, unreachable. A load's is said on no form.
public record HouseholdKeysNotSaved(KeyForm? Form, AdminNotice Notice) : IAction;

public sealed record HouseholdKeysState
{
    // Null until the admin API has answered. Only ever states and last characters: a key typed into
    // a form is never kept here.
    public HouseholdKeys? Keys { get; init; }

    public KeyForm? Saving { get; init; }

    public KeyCheck? Checking { get; init; }

    // The outcome of the last load or save, and the form it was about (none: the load's).
    public AdminNotice? Notice { get; init; }

    public KeyForm? NoticeFor { get; init; }

    // Bumped each time a key is saved, so the form it came from empties its fields.
    public int SavedCount { get; init; }

    public static HouseholdKeysState Initial => new();
}

// The household's keys as the admin area's keys page shows them: what the admin API answered, a save
// shown as saved once the API says so, never before.
public sealed class HouseholdKeysStore : IDisposable
{
    private readonly Store<HouseholdKeysState> _store;

    public HouseholdKeysStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdKeysState>(HouseholdKeysState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdKeysState State => _store.State;

    public IObservable<HouseholdKeysState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdKeysState Reduce(HouseholdKeysState state, IAction action) => action switch
    {
        LoadHouseholdKeys => state with { Notice = null, NoticeFor = null },
        HouseholdKeysLoaded a => state with { Keys = a.Keys },
        SaveOpenRouterKey => Saving(state, KeyForm.OpenRouter),
        SaveIdealistaKeys => Saving(state, KeyForm.Idealista),
        SaveLemonadeBox or RemoveLemonadeBox => Saving(state, KeyForm.Lemonade),
        SaveTelegramBot => Saving(state, KeyForm.Telegram),
        HouseholdKeysSaved a => state with
        {
            Keys = a.Keys,
            Saving = null,
            NoticeFor = a.Form,
            SavedCount = state.SavedCount + 1,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        CheckHouseholdKey a => state with { Checking = new KeyCheck(a.Form, a.AgentId), Notice = null, NoticeFor = null },
        HouseholdKeyChecked a => state with
        {
            Keys = a.Keys,
            Checking = null,
            NoticeFor = a.Form,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Checked: it works.")
        },
        HouseholdKeysNotSaved a => state with { Saving = null, Checking = null, Notice = a.Notice, NoticeFor = a.Form },
        _ => state
    };

    private static HouseholdKeysState Saving(HouseholdKeysState state, KeyForm form) =>
        state with { Saving = form, Notice = null, NoticeFor = null };
}