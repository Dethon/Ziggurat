using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// One turn's steering handle: made by the conversation group as the turn starts, signalled when its own
// person steers it, and read by the tool loop once each tool call returns, which then asks the model
// nothing more. It rides the turn's own run options down to the tool loop's chat options, never an
// ambient, so a worker — whose run builds options of its own — is never cut by its parent's steer.
// It also says whether the channel handed the turn's message back to its sender as it stopped the
// conversation (`isReturned`, asked of the turn's key when the history keeps a stopped turn).
public sealed class TurnSteering(string? turnKey = null, Func<string, bool>? isReturned = null)
{
    public const string OptionsKey = "TurnSteering";

    private int _steered;

    public bool IsSteered => Volatile.Read(ref _steered) == 1;

    public void Steer() => Interlocked.Exchange(ref _steered, 1);

    // A steer whose turn had just started when its sender stopped the reply: the hub had not heard the
    // turn it cut end, so it put the steer back in its sender's composer, and a stopped turn kept as well
    // would give them the steer twice.
    public bool IsReturned => turnKey is not null && isReturned?.Invoke(turnKey) == true;

    private int _settled;

    // The history has decided what the turn keeps — stored it, kept it as stopped, or kept nothing of a
    // turn handed back. A turn stopped before its run reached the history never is, and the conversation
    // group then keeps its messages itself when the channel drew them.
    public bool IsSettled => Volatile.Read(ref _settled) == 1;

    public void Settle() => Interlocked.Exchange(ref _settled, 1);

    public static TurnSteering? Of(ChatOptions? options) =>
        options?.AdditionalProperties?.GetValueOrDefault(OptionsKey) as TurnSteering;
}

// What the conversation group hands the agent for a turn. Not options that replace what the agent builds
// for itself (instructions, tools, the config patch): the agent builds those as for a turn handed none,
// and carries these beside them.
public sealed class TurnRunOptions : AgentRunOptions
{
    public TurnRunOptions()
    {
    }

    private TurnRunOptions(TurnRunOptions options) : base(options)
    {
        Steering = options.Steering;
    }

    public TurnSteering? Steering { get; init; }

    public override AgentRunOptions Clone() => new TurnRunOptions(this);
}