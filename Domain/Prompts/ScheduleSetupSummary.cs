using Domain.Contracts;

namespace Domain.Prompts;

// The agents a schedule can be written for, in the words of the scheduling document's 'setup' part.
public class ScheduleSetupSummary(IAgentCatalog agents)
{
    public string Get()
    {
        var ordered = agents.GetAll().OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var directories = string.Join("\n", ordered.Select(agent => $"/schedules/{agent.Id}"));
        var described = string.Join("\n", ordered.Select(agent =>
            string.IsNullOrWhiteSpace(agent.Description)
                ? $"- `{agent.Id}` ({agent.Name})"
                : $"- `{agent.Id}` ({agent.Name}) — {agent.Description}"));

        // The summary has always ended in a newline, and so has the prompt served with it: kept, so
        // the cached prefix of every conversation that reads it is the same bytes it was.
        return SchedulingPrompt.Setup.Fill(("directories", directories), ("agents", described)) + "\n";
    }
}