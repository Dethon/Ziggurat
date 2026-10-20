namespace Domain.Exceptions;

// A turn the model it resolved to cannot see: what was sent would reach it as nothing at all, and an
// answer that silently ignores what was sent is worse than no answer. Thrown before the request
// leaves, so no retry sends it, and its message is what the channel is told.
public sealed class AttachmentRefusedException(string message) : Exception(message);