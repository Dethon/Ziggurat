using Domain.Judgments;

namespace Tests.Unit.Judgments;

// A judge that answers what it is told and remembers what it was asked. Answers by request, so a
// test can answer one candidate differently from another; a gate that holds every call until the
// expected number are in flight is what a concurrency claim is asserted on.
internal sealed class StubJudge(Func<JudgmentRequest, JudgmentOutcome> answer) : IJudge
{
    private readonly List<JudgmentRequest> _requests = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdUntil;

    public StubJudge(JudgmentOutcome outcome) : this(_ => outcome)
    {
    }

    public static StubJudge Absent(AbsenceReason reason = AbsenceReason.Error) =>
        new(new JudgmentOutcome.Absent(reason));

    public static StubJudge Nouls(params (string Id, double Probability)[] answers) =>
        new(Answered(answers));

    public static JudgmentOutcome Answered(params (string Id, double Probability)[] answers) =>
        new JudgmentOutcome.Answered(new Judgment(
            "jev-test",
            answers.ToDictionary(a => a.Id, a => (JudgmentAnswer)new NoulAnswer(a.Probability), StringComparer.Ordinal),
            new JudgmentUsage(100, 0)));

    public IReadOnlyList<JudgmentRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public int MaxInFlight { get; private set; }

    private int _inFlight;

    // Every call waits until this many are in flight, then all proceed together.
    public StubJudge HoldingUntil(int inFlight)
    {
        _holdUntil = inFlight;
        return this;
    }

    public async Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
    {
        lock (_gate)
        {
            _requests.Add(request);
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
            if (_holdUntil > 0 && _inFlight >= _holdUntil)
            {
                _release.TrySetResult();
            }
        }

        if (_holdUntil > 0)
        {
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline);
        }

        lock (_gate)
        {
            _inFlight--;
        }

        return answer(request);
    }
}