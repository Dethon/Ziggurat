using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Skills;
using Tests.Eval.Fixtures;

namespace Tests.Eval.Harness;

// What one run of a scenario produced. Every assertion in the suite reads this and nothing else,
// so what a scenario checks does not depend on where a tool lives or how the agent is built.
//
// It is the run's metrics sink too: what the turn cost arrives on the same usage events the
// deployment publishes, and a recording that heard them is how a scenario's row on the scorecard
// gets a price.
public sealed class Recording : IToolInvocationObserver, IMetricsPublisher
{
    private readonly List<ToolInvocation> _calls = [];
    private readonly List<ToolInvocation> _workerCalls = [];
    private readonly List<ToolInvocation> _preloads = [];
    private readonly List<Spend> _spends = [];
    private readonly Lock _gate = new();

    // What a preloaded skill's call says where a load's result would be: the body is in the
    // conversation, not on this recording, and the dump has to say who put it there.
    public const string PreloadedResult = "(preloaded by the host: the body was inserted before the model's first call)";

    // The agent the scenario's turn is addressed to, under every name it is heard by, set by the
    // run that built it. Two names, because two things speak for it: a call and a turn name the
    // agent as its metrics do ("Jonas"), and a preload — published by the judge's own client —
    // names it as its session does ("Jonas-<conversation>"). With real workers the same observer
    // hears the worker too, and everything a scenario declares — the permitted set, the ceiling,
    // the prompt the dump shows — is about this agent alone. Empty where nobody named one: then
    // nothing is a worker's, which is every recording a test builds by hand.
    public IReadOnlyList<string> Parent { get; init; } = [];

    private bool IsWorkers(string? agentId) =>
        Parent.Count > 0 && agentId is not null && !Parent.Contains(agentId, StringComparer.Ordinal);

    // What the workers did with what they were handed, in the order it was heard. Never checked
    // against the scenario's contract: it is what the dump and the judge read to see whether a
    // delegated answer was researched or made up.
    public IReadOnlyList<ToolInvocation> WorkerCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _workerCalls];
            }
        }
    }

    // Sorted by the position the seam stamped, not by the order the observations arrived:
    // concurrent invocations complete in whichever order they finish, and the contract a
    // scenario declares is about the order they were issued in.
    public IReadOnlyList<ToolInvocation> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.OrderBy(c => c.Sequence).ToArray();
            }
        }
    }

    // What the turn was sent with and what answered it. Last write wins: a scenario is one turn,
    // and a harness that re-used a recording across turns would be reporting the first turn's
    // prompt beside the last turn's calls.
    public string? SystemPrompt { get; private set; }

    public ServedRoute? Route { get; private set; }

    // The home before and after the turn, set by the runner that drove it. Two snapshots rather
    // than a list of observed calls, because the question a scenario asks — did anything else
    // move — is only answerable against the whole home: a service call whose script cascades into
    // three devices reports one call and changes three states.
    public IReadOnlyDictionary<string, string> StateBefore { get; set; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> StateAfter { get; set; } =
        new Dictionary<string, string>();

    // The vault before and after the turn, keyed by the path the agent sees. Notes are asserted on
    // by their own text rather than through the calls that wrote them: a surgical edit and a
    // whole-file rewrite are the same tool call, and only the file afterwards says which happened.
    public IReadOnlyDictionary<string, string> FilesBefore { get; set; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> FilesAfter { get; set; } =
        new Dictionary<string, string>();

    // What the user is still remembered as having said once the turn is over, set by the runner
    // that drove it. Read as a whole rather than as a diff of the calls: `memory_forget` takes a
    // query and deletes everything the search matched, so one call can empty the store and the
    // call log looks the same either way.
    public IReadOnlyList<string> MemoriesAfter { get; set; } = [];

    // The work this turn handed to a worker, set by the runner that drove it. It does not come
    // through the tool seam usefully: the call is there, but which profile ran and what the parent
    // wrote in the prompt are what a delegation scenario is about.
    public IReadOnlyList<Delegation> Delegations { get; set; } = [];

    // The agent's own answer, set by the runner that drove it: it comes back as the response
    // rather than through the seam, because a reply is what the agent returned and not something
    // observed on its way past.
    public string Reply { get; set; } = "";

    // The reply as a channel reads it: the last message that said anything, where Reply joins
    // every message the turn wrote. A structured answer is parsed from this one.
    public string? Answer { get; set; }

    // How long the turn took, from the request going out to the answer, without the stack.
    public TimeSpan Elapsed { get; set; }

    // The attachments of the turn whose bytes the model was sent at least once.
    public IReadOnlyList<string> AttachmentsServed { get; set; } = [];

    // The run hit its own deadline. Kept on the recording rather than thrown, so a scenario that
    // times out lands on the scorecard as a failure with a reason instead of vanishing from it.
    public bool TimedOut { get; set; }

    // The provider refused the turn — a 5xx, a dropped connection — so the model never answered.
    // Kept for the same reason as TimedOut: a thrown provider error dropped the scenario off the
    // scorecard as one nobody ran.
    public string? ProviderError { get; set; }

    // What the turn moved, computed in one place: the checks decide whether it was declared and
    // the dump prints it, and two copies of "different from before" is one copy too many.
    public IReadOnlyDictionary<string, string> Moved =>
        StateAfter
            .Where(entry => StateBefore.GetValueOrDefault(entry.Key) != entry.Value)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    public void OnTurn(TurnObservation turn)
    {
        if (IsWorkers(turn.AgentId))
        {
            return;
        }

        lock (_gate)
        {
            SystemPrompt = turn.SystemPrompt ?? SystemPrompt;
            Route = turn.Route ?? Route;
        }
    }

    public void OnInvoked(ToolInvocation invocation)
    {
        lock (_gate)
        {
            (IsWorkers(invocation.AgentId) ? _workerCalls : _calls).Add(invocation);
        }
    }

    // The skills the host put into the conversation before the model's first call, as the calls
    // a load would have left, so a required load is met by either loader and an unpermitted
    // preload is as visible as an unpermitted load. Sequenced below zero: they sat before
    // anything the model asked for.
    public IReadOnlyList<ToolInvocation> Preloads
    {
        get
        {
            lock (_gate)
            {
                return [.. _preloads];
            }
        }
    }

    public bool IsPreload(ToolInvocation call) => call.Sequence < 0;

    // The judge that answered this run, for the scorecard: null where nothing was judged.
    public string? PreloadModel { get; private set; }

    // Every judgment's outcome, counted, so a pass where the judge timed out or errored every
    // time is legible on the scorecard rather than indistinguishable from the preload being off.
    private readonly Dictionary<string, int> _preloadOutcomes = [];

    public IReadOnlyDictionary<string, int> PreloadOutcomes
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_preloadOutcomes);
            }
        }
    }

    // Every request the run paid for — the agent's turns and the judge's verdicts alike — summed.
    public Spend Spend
    {
        get
        {
            lock (_gate)
            {
                return Spend.Sum(_spends);
            }
        }
    }

    // What the exec screen was asked and what it put to the person, over this run.
    private ExecScreenTally _screens = ExecScreenTally.None;

    public ExecScreenTally Screens
    {
        get
        {
            lock (_gate)
            {
                return _screens;
            }
        }
    }

    // The usage events are money — every model's, Jev's among them, whose client publishes one
    // per judgment as a chat turn does — and a preload is the one other event a scenario asserts
    // on: it cannot come through the tool seam, which only sees calls the model emitted. The rest
    // of the stream, a judgment's own event included, is the deployment's telemetry.
    public void Publish(MetricEvent metricEvent)
    {
        switch (metricEvent)
        {
            case TokenUsageEvent usage:
                lock (_gate)
                {
                    _spends.Add(Spend.Of(usage));
                }

                break;
            case ExecScreenEvent screened:
                lock (_gate)
                {
                    _screens += ExecScreenTally.Of(screened);
                }

                break;
            // A worker's preload sat before the worker's first call, not the turn's: the money
            // above is the run's whoever spent it, the calls a scenario asserts on are not.
            case SkillPreloadEvent preload when !IsWorkers(preload.AgentId):
                lock (_gate)
                {
                    PreloadModel = preload.Model ?? PreloadModel;
                    _preloadOutcomes[preload.Outcome] = _preloadOutcomes.GetValueOrDefault(preload.Outcome) + 1;
                    // The loads first, then the reads the host made beside them, all below zero
                    // and in the order the host wrote them.
                    var count = preload.Skills.Count + preload.Reads.Count;
                    var loads = preload.Skills
                        .Select((skill, index) => new ToolInvocation
                        {
                            Sequence = index - count,
                            ToolName = EvalTools.LoadSkill,
                            Arguments = JsonSerializer.Serialize(
                                new Dictionary<string, string> { [SkillsProvider.SkillNameParameter] = skill }),
                            Result = PreloadedResult,
                            Outcome = ToolInvocationOutcome.Completed
                        })
                        .Concat(preload.Reads.Select((path, index) => new ToolInvocation
                        {
                            Sequence = index + preload.Skills.Count - count,
                            ToolName = EvalTools.Read,
                            Arguments = JsonSerializer.Serialize(
                                new Dictionary<string, string> { [VfsFileReadTool.FilePathParameter] = path }),
                            Result = PreloadedResult,
                            Outcome = ToolInvocationOutcome.Completed
                        }))
                        .ToList();
                    _preloads.AddRange(loads);
                    _calls.AddRange(loads);
                }

                break;
        }
    }
}