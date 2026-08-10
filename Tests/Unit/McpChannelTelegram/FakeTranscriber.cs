using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Tests.Unit.McpChannelTelegram;

// The seam for every channel test: a Telegram test fails for reasons about Telegram, never about
// whisper. What the adapter itself does against Lemonade is pinned once, at the HTTP boundary, in
// LemonadeTranscriptionClientTests.
internal sealed class FakeTranscriber : IAudioTranscriber
{
    private readonly List<TranscriptionRequest> _requests = [];

    public IReadOnlyList<TranscriptionRequest> Requests => _requests;
    public TranscriptionResult Result { get; set; } = new() { Text = "" };
    public Exception? Fails { get; set; }

    public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        _requests.Add(request);
        return Fails is null ? Task.FromResult(Result) : Task.FromException<TranscriptionResult>(Fails);
    }
}