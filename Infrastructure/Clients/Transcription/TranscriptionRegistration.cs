using Domain.Contracts;
using Infrastructure.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Transcription;

// Becoming a transcribing host is one call: the named Lemonade HttpClient and the shared client
// behind IAudioTranscriber, registered together so a server cannot hold the client without the
// registration it posts on. What it hears is counted per household (MeteredAudioTranscriber).
public static class TranscriptionRegistration
{
    public static IServiceCollection AddLemonadeTranscription(
        this IServiceCollection services, TranscriptionClientConfig config)
    {
        services.AddHttpClient(LemonadeTranscriptionClient.ClientName);
        return services.AddSingleton<IAudioTranscriber>(sp => new MeteredAudioTranscriber(
            new LemonadeTranscriptionClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                config,
                sp.GetRequiredService<ILogger<LemonadeTranscriptionClient>>()),
            sp.GetService<IMetricsPublisher>() ?? NoOpMetricsPublisher.Instance));
    }
}