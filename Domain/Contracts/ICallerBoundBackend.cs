using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A backend that tells the server behind it who is calling. A tool call carries its turn's
// conversation context with it; an operation the exec bridge makes for a sandbox command runs on
// the bridge's own request, with no turn in flight, so the bridge hands the caller over explicitly
// and gets the backend as that caller sees it.
public interface ICallerBoundBackend
{
    IFileSystemBackend As(ConversationContext? caller);
}