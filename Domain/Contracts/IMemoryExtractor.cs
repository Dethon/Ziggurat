using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

// The payer is whoever the extraction is for: the household of the turn it reads, on its key.
public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, ModelPayer payer, CancellationToken ct);
}