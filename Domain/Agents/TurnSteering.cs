using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// One turn's steering handle: made by the conversation group as the turn starts, signalled when its own
// person steers it, and read by the tool loop once each tool call returns, which then asks the model
// nothing more. It rides the turn's own run options down to the tool loop's chat options, never an
// ambient, so a worker — whose run builds options of its own — is never cut by its parent's steer.
public sealed class TurnSteering
{
    public const string OptionsKey = "TurnSteering";

    private int _steered;

    public bool IsSteered => Volatile.Read(ref _steered) == 1;

    public void Steer() => Interlocked.Exchange(ref _steered, 1);

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