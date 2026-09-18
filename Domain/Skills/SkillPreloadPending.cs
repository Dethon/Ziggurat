using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// A preload started where the user message is built, riding on that message until the skills
// provider takes it at insertion. Beside the message rather than in its properties, because the
// message is persisted and a task is not a property: weakly keyed, so a message nobody ran takes
// its pending result with it, and taken once, so a late look finds nothing to apply.
public static class SkillPreloadPending
{
    private static readonly ConditionalWeakTable<ChatMessage, Task<SkillPreload>> _pending = [];

    public static void Attach(ChatMessage message, Task<SkillPreload> preload) =>
        _pending.AddOrUpdate(message, preload);

    public static Task<SkillPreload>? TryTake(ChatMessage message)
    {
        if (!_pending.TryGetValue(message, out var preload))
        {
            return null;
        }

        _pending.Remove(message);
        return preload;
    }
}