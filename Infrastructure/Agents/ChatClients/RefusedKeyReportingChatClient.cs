using System.ClientModel;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents.ChatClients;

// Around the OpenRouter client of a turn sent on a household's own key: a call OpenRouter answers 401
// — the key itself refused — is said to the accounts service, which marks the key rejected on the
// owner's keys page and asks OpenRouter about it again, and the call fails as it would have. Said
// once per client and never waited for: the turn's failure is the person's answer, and the report a
// courtesy to the owner. Nothing of the key travels; the service checks the key it holds now.
public sealed class RefusedKeyReportingChatClient(
    IChatClient inner, string householdId, IHouseholdKeyReports reports, ILogger? logger = null)
    : DelegatingChatClient(inner)
{
    private int _reported;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            Report();
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool more;
            try
            {
                more = await updates.MoveNextAsync();
            }
            catch (ClientResultException ex) when (ex.Status == 401)
            {
                Report();
                throw;
            }

            if (!more)
            {
                yield break;
            }

            yield return updates.Current;
        }
    }

    private void Report()
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await reports.OpenRouterKeyRefusedAsync(householdId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not tell the accounts service that household {Household}'s OpenRouter key was refused",
                    householdId);
            }
        });
    }
}