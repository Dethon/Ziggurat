using Infrastructure.Clients.Transcription;

namespace McpChannelVoice.Services;

// Named registration for the Lemonade OpenAI-compatible endpoints. Clients are created per call
// (never cached in the singleton services) so IHttpClientFactory handler rotation keeps working.
// The name is the shared client's, so the hub's TTS and its transcriptions cannot end up on two
// differently configured registrations.
public static class LemonadeHttp
{
    public const string ClientName = LemonadeTranscriptionClient.ClientName;
}