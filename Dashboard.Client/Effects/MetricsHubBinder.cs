using System.Collections;
using Dashboard.Client.Contracts;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Logging;

namespace Dashboard.Client.Effects;

// The mapping from a server push to a store update and a family refresh, and nothing else. The
// connection lifecycle belongs to MetricsLiveConnection, which drives Bind and Unbind.
public sealed class MetricsHubBinder(
    MetricFamilyTable families,
    MetricsStore metricsStore,
    HealthStore healthStore,
    ILogger<MetricsHubBinder> logger)
{
    private readonly List<IDisposable> _subscriptions = [];
    private Queue<Func<Task>>? _held;
    private int _holdDepth;

    // Catch-up replaces the event lists wholesale while pushes keep arriving: a push applied before
    // the response lands is erased by the older snapshot, and one the snapshot already contains
    // would be appended on top of its own copy. So the live connection holds pushes for the
    // duration of a catch-up and releases them against the reloaded lists, where each held event is
    // skipped exactly when the snapshot already delivered it — these events carry no id, so having
    // the same values is the identity (see Holds). Skipping drops the whole push, its summary
    // counters included, because catch-up
    // reloads those totals from the server too and they already count the event. Holds nest,
    // because a reconnect can land while a catch-up is still holding: the overlapping hold shares
    // the queue instead of discarding it, and only the last release delivers.
    public void HoldPushes()
    {
        _holdDepth++;
        _held ??= new Queue<Func<Task>>();
    }

    public async Task ReleaseHeldPushesAsync()
    {
        _holdDepth = Math.Max(0, _holdDepth - 1);

        // Drained in place rather than swapped out: each delivery is awaited, and a push arriving
        // in that gap must land behind the queue, not ahead of it — so the hold stays visible to
        // OnPush until the queue is empty. A new hold beginning mid-drain pauses the drain; its own
        // release finishes it.
        try
        {
            while (_holdDepth == 0 && _held is { Count: > 0 } held)
            {
                var deliver = held.Dequeue();
                try
                {
                    await deliver();
                }
                catch (Exception exception)
                {
                    // One push that cannot be applied — a subscribed component throwing while it
                    // renders — is one push lost, not the end of live updates. Letting it out of
                    // here left the queue in place with nothing to drain it and took the throw all
                    // the way out through ConnectAsync, which catches nothing.
                    logger.LogWarning(exception, "Held metrics push could not be applied");
                }
            }
        }
        finally
        {
            if (_holdDepth == 0)
            {
                _held = null;
            }
        }
    }

    private Func<T, Task> OnPush<T>(Func<T, bool> alreadyCaughtUp, Func<T, Task> apply) =>
        evt =>
        {
            if (_held is { } held)
            {
                // The dedupe question is asked on release, once the snapshot has written the list
                // this event may already be in.
                held.Enqueue(() => alreadyCaughtUp(evt) ? Task.CompletedTask : apply(evt));
                return Task.CompletedTask;
            }

            return apply(evt);
        };

    // Record value equality compares a list or a dictionary member by reference, and the snapshot's
    // copy of an event is deserialized separately from the hub's — so for the three event types
    // that carry collections (skill preload, memory judgment, dreaming's refused merges) `Contains`
    // answered false for an event the catch-up had already delivered, and the held push landed on
    // top of its own copy. These events carry no id, so the identity is "the same values", and
    // that is what this asks: the record's own equality first, then a structural walk of whatever
    // members it compared by reference. Only runs while a catch-up is holding.
    private static bool Holds<T>(IEnumerable<T> caughtUp, T evt) where T : MetricEvent =>
        caughtUp.Any(held => held is not null && SameEvent(held, evt));

    private static bool SameEvent<T>(T a, T b) where T : MetricEvent =>
        ReferenceEquals(a, b)
        || (a.GetType() == b.GetType() && a.GetType().GetProperties().All(p => SameValue(p.GetValue(a), p.GetValue(b))));

    private static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string x, string y) => x == y,
        (IDictionary x, IDictionary y) => SameDictionary(x, y),
        (IEnumerable x, IEnumerable y) => SameSequence(x, y),
        _ => a.Equals(b)
    };

    private static bool SameDictionary(IDictionary a, IDictionary b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        return a.Keys.Cast<object>().All(key => b.Contains(key) && SameValue(a[key], b[key]));
    }

    private static bool SameSequence(IEnumerable a, IEnumerable b) =>
        a.Cast<object?>().SequenceEqual(b.Cast<object?>(), EqualityComparer<object?>.Create(SameValue));

    // The live-update path's failure policy, written once: a refresh that fails leaves the family's
    // breakdown at its last known value. Nothing cancels a refresh any more, so a request abandoned
    // on an HTTP timeout settles the same way as any other failure and needs no arm of its own.
    private static async Task RefreshAsync(MetricFamily family)
    {
        try
        {
            await family.RefreshAsync();
        }
        catch { /* Breakdown stays at last known value */ }
    }

    public void Bind(IMetricsHubConnection hub)
    {
        ArgumentNullException.ThrowIfNull(hub);

        _subscriptions.Add(hub.On("OnMemoryRecall", OnPush<MemoryRecallEvent>(
            evt => Holds(families.Memory.Store.State.RecallEvents, evt),
            async evt =>
            {
                metricsStore.IncrementMemoryRecall(evt.MemoryCount);
                families.Memory.Store.AppendRecallEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnMemoryExtraction", OnPush<MemoryExtractionEvent>(
            evt => Holds(families.Memory.Store.State.ExtractionEvents, evt),
            async evt =>
            {
                metricsStore.IncrementMemoryExtraction(evt.StoredCount);
                families.Memory.Store.AppendExtractionEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnMemoryDreaming", OnPush<MemoryDreamingEvent>(
            evt => Holds(families.Memory.Store.State.DreamingEvents, evt),
            async evt =>
            {
                metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
                families.Memory.Store.AppendDreamingEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnMemoryJudgment", OnPush<MemoryJudgmentEvent>(
            evt => Holds(families.Memory.Store.State.JudgmentEvents, evt),
            async evt =>
            {
                families.Memory.Store.AppendJudgmentEvent(evt);
                await RefreshAsync(families.Memory);
            })));

        _subscriptions.Add(hub.On("OnTokenUsage", OnPush<TokenUsageEvent>(
            evt => Holds(families.Tokens.Store.State.Events, evt),
            async evt =>
            {
                metricsStore.IncrementFromTokenUsage(evt);
                families.Tokens.Store.AppendEvent(evt);
                await RefreshAsync(families.Tokens);
            })));

        // A truncation is a counter bump with no event to reconcile by. Catch-up reloads the total
        // from the server, so a held bump is dropped rather than doubled on top of that total; one
        // truncated turn missed here corrects itself on the next load.
        _subscriptions.Add(hub.On("OnContextTruncation", OnPush<ContextTruncationEvent>(
            _ => true,
            async _ =>
            {
                families.Tokens.Store.IncrementTruncations();
                await RefreshAsync(families.Tokens);
            })));

        _subscriptions.Add(hub.On("OnToolCall", OnPush<ToolCallEvent>(
            evt => Holds(families.Tools.Store.State.Events, evt),
            async evt =>
            {
                metricsStore.IncrementToolCall(!evt.Success);
                families.Tools.Store.AppendEvent(evt);
                await RefreshAsync(families.Tools);
            })));

        _subscriptions.Add(hub.On("OnError", OnPush<ErrorEvent>(
            evt => Holds(families.Errors.Store.State.Events, evt),
            async evt =>
            {
                families.Errors.Store.AppendEvent(evt);
                await RefreshAsync(families.Errors);
            })));

        _subscriptions.Add(hub.On("OnScheduleExecution", OnPush<ScheduleExecutionEvent>(
            evt => Holds(families.Schedules.Store.State.Events, evt),
            async evt =>
            {
                families.Schedules.Store.AppendEvent(evt);
                await RefreshAsync(families.Schedules);
            })));

        _subscriptions.Add(hub.On("OnLatency", OnPush<LatencyEvent>(
            evt => Holds(families.Latency.Store.State.Events, evt),
            async evt =>
            {
                families.Latency.Store.AppendEvent(evt);
                await RefreshAsync(families.Latency);
            })));

        _subscriptions.Add(hub.On("OnVoice", OnPush<VoiceEvent>(
            evt => Holds(families.Voice.Store.State.Events, evt),
            async evt =>
            {
                families.Voice.Store.AppendEvent(evt);
                await RefreshAsync(families.Voice);
            })));

        _subscriptions.Add(hub.On("OnSkillPreload", OnPush<SkillPreloadEvent>(
            evt => Holds(families.Skills.Store.State.Events, evt),
            async evt =>
            {
                families.Skills.Store.AppendEvent(evt);
                await RefreshAsync(families.Skills);
            })));

        _subscriptions.Add(hub.On("OnModalDismissal", OnPush<ModalDismissalEvent>(
            evt => Holds(families.Web.Store.State.Events, evt),
            async evt =>
            {
                families.Web.Store.AppendEvent(evt);
                await RefreshAsync(families.Web);
            })));

        // Health is an upsert, so there is no copy of it in the roster catch-up reloads to
        // reconcile against: nothing is ever skipped, and holding is what puts the push after the
        // roster rather than under it. A service that came back while the roster was being read
        // would otherwise show red until its next heartbeat.
        _subscriptions.Add(hub.On("OnHealthUpdate", OnPush<ServiceHealthUpdate>(_ => false, evt =>
        {
            var current = healthStore.State.Services.ToList();
            var idx = current.FindIndex(s => s.Service == evt.Service);
            var entry = new ServiceHealth(evt.Service, evt.IsHealthy, evt.Timestamp.ToString("o"));

            if (idx >= 0)
            {
                current[idx] = entry;
            }
            else
            {
                current.Add(entry);
            }

            healthStore.UpdateHealth(current);
            return Task.CompletedTask;
        })));
    }

    // Letting go of the pushes means letting go of the ones still queued too. A module disposed
    // mid-catch-up used to leave the hold standing, so the queued closures kept their store
    // references and whatever bound next inherited a hold nobody had asked for.
    public void Unbind()
    {
        _subscriptions.ForEach(subscription => subscription.Dispose());
        _subscriptions.Clear();
        _holdDepth = 0;
        _held = null;
    }
}