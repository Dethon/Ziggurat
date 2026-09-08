namespace Domain.Prompts;

// The prose that explains the advertised list, in the base prompt of every agent that has a skill.
// The framework appends the bare list at the tail of the instructions; this is where the model is
// told what the list is and when to load from it. It says nothing about which skill applies — that
// is the description's job, and the eval tests it as a claim.
public static class SkillsPrompt
{
    public const string Instructions =
        """
        ## Skills

        Some of what you know is packaged as skills, listed at the end of these instructions as `<skill>` entries with a name and a one-line description each. A skill is the doing guide for one kind of task — the file shapes, the arguments, how a result is read — and it is not in front of you until you load it. When a request calls for one, call `load_skill` with its name before you act, in the same turn, and follow what it returns. Load a skill once per conversation: its text stays in the conversation, so never load it again. Load only the skills the request calls for, by exactly one of the listed names — the rules that choose between mechanisms are already here, a skill the request does not need is a call nobody needs, and a request that matches no listed skill makes no load at all.
        """;
}