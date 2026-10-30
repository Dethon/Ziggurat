using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A backend that tells the server behind it who is calling, and only as somebody hands the caller
// over: a file tool the caller its call's arguments carry (ToolCaller), the exec bridge the caller of
// the command whose operations it answers. Each gets the backend as that caller sees it; the backend
// itself acts for nobody.
public interface ICallerBoundBackend
{
    IFileSystemBackend As(ConversationContext? caller);
}