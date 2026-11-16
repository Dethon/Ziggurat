using System.ClientModel;
using System.Runtime.CompilerServices;
using Domain.Exceptions;
using Domain.Monitor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents.ChatClients;

// Around the OpenRouter client of a turn sent on a household's own key: a call OpenRouter answers 401
// — the key itself refused — is said to the accounts service, which marks the key rejected on the
// owner's keys page and asks OpenRouter about it again. Said once per client and never waited for:
// the report is a courtesy to the owner. Nothing of the key travels; the service checks the key it
// holds now. The call fails as a refusal the person is told in words an owner can act on — the key
// refused, or (402) the household's account out of credit — rather than as the provider's status
// line, which read "HTTP 401 (: ) User not found." in the chat.
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
        catch (ClientResultException ex) when (Refusal(ex) is { } answer)
        {
            throw Refused(ex, answer);
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
            catch (ClientResultException ex) when (Refusal(ex) is { } answer)
            {
                throw Refused(ex, answer);
            }

            if (!more)
            {
                yield break;
            }

            yield return updates.Current;
        }
    }

    private static string? Refusal(ClientResultException ex) => ex.Status switch
    {
        401 => RefusalAnswers.KeyRefused,
        402 => RefusalAnswers.OutOfCredit,
        _ => null
    };

    private TurnRefusedException Refused(ClientResultException ex, string answer)
    {
        if (ex.Status == 401)
        {
            Report();
        }

        return new TurnRefusedException(answer, ex);
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