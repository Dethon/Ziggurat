namespace Domain.Contracts;

// What a turn's recall leaves to be done once the turn is over: the extraction of what the person
// just said. The conversation group queues it only when the turn worked — a turn refused for the
// household's key, its credit or a model error would be extracted on the same refused key, and
// its retry is extracted in its place.
public delegate ValueTask MemoryExtraction(CancellationToken ct);