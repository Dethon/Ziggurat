using Domain.Contracts;
using Domain.DTOs.Channel;

namespace McpChannelVoice.Services;

// Outcome of an attention handoff. The two non-moving cases are deliberately distinct because only
// one of them means a handoff was refused: DeclinedStale is the winner's own turn having already
// run, while NothingToMove is the ordinary first-utterance steal — the holder had no conversation
// yet, so the attention moved with nothing to carry.
public enum BindingTransfer
{
    Moved,
    DeclinedStale,
    NothingToMove
}

// One active conversation per satellite. Each utterance renews a TimeProvider-based
// idle timer; when it fires, the in-memory mapping is dropped so the next utterance
// mints a fresh conversation. Persisted Redis history and the WebChat topic are left
// intact (the timer only clears local routing state).
public sealed class VoiceConversationManager(
    IConversationFactory factory,
    ReplyTextAccumulator accumulator,
    TimeProvider time,
    TimeSpan lifetime,
    ILogger<VoiceConversationManager> logger)
{
    private sealed record Entry(string ConversationId, ITimer Timer, long Generation, long BoundAt);

    private readonly Dictionary<string, Entry> _bySatellite = new();
    private readonly Dictionary<string, string> _conversationToSatellite = new();
    private readonly Lock _gate = new();
    private long _generation;

    // firstUtterance only seeds the new conversation's WebChat topic (its InitialPrompt);
    // the caller still dispatches the utterance itself via the channel using the returned id.
    //
    // The lock is released across factory.CreateAsync (I/O) and re-acquired with a second
    // existence check. In practice a satellite's utterances are processed sequentially by its
    // own connection loop, so concurrent calls for the same satellite don't occur; the
    // double-check is a cheap safeguard that discards a redundant creation if they ever do.
    //
    // The speaker is who opens a conversation, so a minted one waits for them: its topic is a
    // recognised speaker's own and the guest's the household's. A conversation that already exists
    // never waits, which keeps every later turn's resolution beside the rest of its dispatch.
    public async Task<string> GetOrCreateAsync(
        SatelliteSession session, string agentId, string firstUtterance, CancellationToken ct,
        Task<(Principal Principal, string Sender)>? speaker = null)
    {
        lock (_gate)
        {
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                Renew(session.SatelliteId, existing);
                return existing.ConversationId;
            }
        }

        var (principal, sender) = speaker is null ? ((Principal?)null, session.Config.Identity) : await speaker;
        var creation = await factory.CreateAsync(
            new CreateConversationParams
            {
                AgentId = agentId,
                TopicName = $"{session.Config.Identity} @ {session.Config.Room}",
                Sender = sender,
                InitialPrompt = firstUtterance,
                Principal = principal
            },
            ct);

        lock (_gate)
        {
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                Renew(session.SatelliteId, existing);
                return existing.ConversationId;
            }

            var satelliteId = session.SatelliteId;
            var generation = ++_generation;
            var timer = time.CreateTimer(_ => Expire(satelliteId, generation), null, lifetime, Timeout.InfiniteTimeSpan);
            _bySatellite[satelliteId] = new Entry(creation.Identity.ConversationId, timer, generation, time.GetTimestamp());
            _conversationToSatellite[creation.Identity.ConversationId] = satelliteId;
            logger.LogInformation(
                "Voice conversation {ConversationId} opened for satellite {Satellite}",
                creation.Identity.ConversationId, satelliteId);
            return creation.Identity.ConversationId;
        }
    }

    public string? GetActiveConversationId(string satelliteId)
    {
        lock (_gate)
        {
            return _bySatellite.TryGetValue(satelliteId, out var entry) ? entry.ConversationId : null;
        }
    }

    public string? ResolveSatelliteId(string conversationId)
    {
        lock (_gate)
        {
            return _conversationToSatellite.GetValueOrDefault(conversationId);
        }
    }

    // Attention handoff: the user re-woke on another satellite mid-conversation, so the
    // conversation (and its idle timer) follows them. The displaced target entry — if the winner
    // had its own idle conversation — is simply dropped; it would have idle-expired anyway.
    // claimedAt (the wake claim's arrival timestamp) guards that displacement: a target binding
    // newer than the claim means the winner's turn already ran independently while the decision
    // was delayed, and the agent's reply to that binding may still be in flight — displacing it
    // would silently drop the reply, so the stale handoff is skipped instead.
    public BindingTransfer TransferBinding(string fromSatelliteId, string toSatelliteId, long claimedAt)
    {
        lock (_gate)
        {
            if (_bySatellite.TryGetValue(toSatelliteId, out var target) && target.BoundAt >= claimedAt)
            {
                return BindingTransfer.DeclinedStale;
            }

            // Bindings are minted at dispatch, so a holder still mid-capture owns one only if an
            // earlier utterance of the same conversation already dispatched. Having none is the
            // normal first-utterance case, not a refusal.
            if (!_bySatellite.Remove(fromSatelliteId, out var entry))
            {
                return BindingTransfer.NothingToMove;
            }

            if (_bySatellite.Remove(toSatelliteId, out var displaced))
            {
                displaced.Timer.Dispose();
                _conversationToSatellite.Remove(displaced.ConversationId);
            }

            Renew(toSatelliteId, entry);
            _conversationToSatellite[entry.ConversationId] = toSatelliteId;
            logger.LogInformation(
                "Voice conversation {ConversationId} handed off {From} -> {To}",
                entry.ConversationId, fromSatelliteId, toSatelliteId);
            return BindingTransfer.Moved;
        }
    }

    // Re-create the idle timer on each renewal so the firing callback carries the generation it was
    // armed with; a stale fire that lost the race to a renewal sees a newer generation and no-ops.
    private void Renew(string satelliteId, Entry existing)
    {
        existing.Timer.Dispose();
        var generation = ++_generation;
        var timer = time.CreateTimer(_ => Expire(satelliteId, generation), null, lifetime, Timeout.InfiniteTimeSpan);
        _bySatellite[satelliteId] = existing with { Timer = timer, Generation = generation, BoundAt = time.GetTimestamp() };
    }

    private void Expire(string satelliteId, long generation)
    {
        lock (_gate)
        {
            if (!_bySatellite.TryGetValue(satelliteId, out var entry) || entry.Generation != generation)
            {
                return;
            }

            _bySatellite.Remove(satelliteId);
            _conversationToSatellite.Remove(entry.ConversationId);
            entry.Timer.Dispose();
            // Discard, not Flush: an announcement spoken beside a live turn buffers under that
            // turn, and one whose stream never terminated is only ever reached here.
            accumulator.Discard(entry.ConversationId);
            logger.LogInformation(
                "Voice conversation {ConversationId} expired for satellite {Satellite}",
                entry.ConversationId, satelliteId);
        }
    }
}