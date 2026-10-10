using Domain.Prompts;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// The load tool as the thread records it: its name, its one argument, how a loaded skill is
// recognised in a history and how a preload is written into one. The framework owns the tool
// (Microsoft.Agents.AI's `AgentSkillsProvider`), which Domain does not reference, so the names
// are spelled here and a test pins them to the framework's.
//
// A preload is recorded as the framework's own load would have left it — an assistant message
// carrying the call, a tool message carrying the wrapped body — so "already loaded" is one
// question asked of the history, whoever loaded and whichever build stored it, with no second
// record to drift. That record is the thread's and never the wire's: a model is sent a note in
// its place (`SkillPreloadNote`), because a call it did not make is not something every model
// or provider takes.
public static class SkillLoadTool
{
    public const string Name = "load_skill";

    public const string SkillNameParameter = "skillName";

    // Against the persisted thread, which is not always what the model is sent: the truncator
    // drops the oldest message groups at send time while Redis keeps them, so in a conversation
    // past the context window a load pair can still be here and no longer in front of the model.
    // The preload then does not ask about that skill again. It fails toward the old behaviour —
    // the model loads it for itself, as it did before any of this — and the alternative is
    // teaching this side the truncator's cut, which is a send-time decision it cannot see.
    public static IReadOnlySet<string> LoadedIn(IEnumerable<ChatMessage> history) =>
        history
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(c => string.Equals(c.Name, Name, StringComparison.Ordinal))
            .Select(SkillNameOf)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    // The two messages a load leaves: one call per skill on one assistant message, and one
    // result per call on one tool message.
    public static IReadOnlyList<ChatMessage> AsLoaded(IReadOnlyList<PromptSkill> skills, string callIdPrefix) =>
        AsPreloaded(new SkillPreload(SkillPreloadOutcome.Preloaded, skills), callIdPrefix);

    // The same two messages for a whole preload: the loads first, then the reads the skills
    // declared, on the same pair — the shape a model that loads and reads in one call leaves.
    // The read is the filesystem tool's own call, by its callable name and argument, with what
    // the tool answered.
    public static IReadOnlyList<ChatMessage> AsPreloaded(SkillPreload preload, string callIdPrefix)
    {
        var loads = preload.Skills
            .Select(skill => (
                Name,
                Arguments: new Dictionary<string, object?> { [SkillNameParameter] = skill.Name },
                Result: (object)Wrapped(skill)));
        var reads = preload.Reads
            .Select(read => (
                Name: ReadToolName,
                Arguments: new Dictionary<string, object?> { [VfsFileReadTool.FilePathParameter] = read.Path },
                Result: (object)read.Result));
        var pairs = loads.Concat(reads)
            .Select((pair, i) => (
                Call: new FunctionCallContent($"{callIdPrefix}-{i + 1}", pair.Name, pair.Arguments),
                pair.Result))
            .ToList();

        return
        [
            new ChatMessage(ChatRole.Assistant, [.. pairs.Select(p => p.Call)]),
            new ChatMessage(ChatRole.Tool, [.. pairs.Select(p => new FunctionResultContent(p.Call.CallId, p.Result))])
        ];
    }

    // What marks a call as the host's, and the head of every id this build writes: one prefix
    // per turn (`preload-1a2b3c4d`), then the call's ordinal.
    public const string HostCallMark = "preload-";

    // A call the host recorded, under any id it was ever stored with — nothing is migrated, so
    // all three schemes are in somebody's thread: `preload-<turn>-<n>`, the reads once counted
    // apart as `preload-<turn>-read-<n>`, and `<n>-preload-<turn>` from the days the pair went to
    // the wire and Mistral, reading only the first nine letters and digits of an id, refused two
    // calls that agreed that far. No provider issues an id of either form.
    public static bool IsHostCall(string? callId)
    {
        if (callId is null)
        {
            return false;
        }

        var mark = callId.IndexOf(HostCallMark, StringComparison.Ordinal);
        return mark == 0
            || (mark > 1 && callId[mark - 1] == '-' && callId[..(mark - 1)].All(char.IsAsciiDigit));
    }

    public static readonly string ReadToolName = FileSystemToolFeature.Callable(VfsFileReadTool.Name);

    // The framework's wrapper, captured off a real load on Microsoft.Agents.AI 1.20.0 and pinned
    // by a test against one. The empty resource and script elements are there because no skill
    // here ships either (docs/adr/0039).
    public static string Wrapped(PromptSkill skill) =>
        $"<name>{skill.Name}</name>\n" +
        $"<description>{skill.Description}</description>\n" +
        "\n" +
        "<instructions>\n" +
        $"{skill.Body}\n" +
        "</instructions>\n" +
        "\n" +
        "<available_resources />\n" +
        "\n" +
        "<available_scripts />";

    private static string? SkillNameOf(FunctionCallContent call) =>
        call.Arguments is not null && call.Arguments.TryGetValue(SkillNameParameter, out var value)
            ? value?.ToString()
            : null;
}