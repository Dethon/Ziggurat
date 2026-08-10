using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// The transcription route's wire surface, named once. The browser posts here and the channel
// server maps it, so a route or header spelled separately in each is a route that can drift apart
// silently. It is deliberately not part of the attachment paths: a dictation stores nothing.
[PublicAPI]
public static class DictationEndpointPaths
{
    public const string Transcriptions = "/api/dictation";

    public const string TicketHeader = "X-Dictation-Ticket";

    public const string SpaceQueryParameter = "space";
}