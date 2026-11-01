namespace Infrastructure.Agents.ChatClients;

// The agent telling the accounts service that OpenRouter refused a household's own key in a turn, so
// the owner's keys page shows it rejected without waiting for the owner to save it again: the service
// marks it so and asks OpenRouter about it again. It names the household and nothing of the key —
// the service checks the key it holds now.
public interface IHouseholdKeyReports
{
    Task OpenRouterKeyRefusedAsync(string householdId, CancellationToken ct);
}