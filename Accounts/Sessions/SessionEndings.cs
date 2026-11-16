using Accounts.Announcements;
using Domain.DTOs.Channel;

namespace Accounts.Sessions;

// Tells whoever listens that some of a person's sessions ended, so the chat hub closes the
// connections they held at once rather than leave a signed-out page hearing its person's new
// conversations until it next sends (SessionsEnded). Nobody told is logged, never refused: the
// sessions ended all the same, and the hub's next ask for one of them answers nobody.
public sealed class SessionEndings(RedisAnnouncer announcer, ILogger<SessionEndings> logger)
{
    public async Task TellAsync(string tenantId, string userId, CancellationToken ct)
    {
        if (await announcer.AnnounceAsync(SessionsEnded.EndedChannel, new SessionsEnded(tenantId, userId), ct) is { } notTold)
        {
            logger.LogWarning("Sessions of {UserId} ended, but open connections were not told: {Why}", userId, notTold);
        }
    }
}