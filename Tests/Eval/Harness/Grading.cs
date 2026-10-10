namespace Tests.Eval.Harness;

// One run, marked on every dimension its scenario declares. Unlike a judged check, which is only
// asked of a run the deterministic checks passed, a grade is taken on every run that produced an
// answer: a run that went one call over its ceiling still researched something, and a mean over
// the green runs alone is a mean over the runs that flattered the model.
public static class Grading
{
    public static async Task<IReadOnlyList<GradeMark>> MarksAsync(
        Scenario scenario, Recording recording, string apiKey, HttpMessageHandler? transport = null)
    {
        // The provider refusing the turn is nobody's answer, so it is nobody's score.
        if (scenario.Graded.Count == 0 || recording.ProviderError is not null)
        {
            return [];
        }

        // A deadline is the model's own, and no answer is the worst one.
        if (recording.TimedOut)
        {
            return [.. scenario.Graded.Select(grade => new GradeMark(grade.Dimension, 0, "the run never answered"))];
        }

        var marks = await Task.WhenAll(scenario.Graded.Select(grade => grade switch
        {
            FactGrade facts => Task.FromResult<GradeMark?>(
                new GradeMark(facts.Dimension, facts.Score(recording.Reply))),
            GroundedGrade grounded => Task.FromResult<GradeMark?>(
                new GradeMark(grounded.Dimension, grounded.Score(recording))),
            JudgedGrade judged => Judge.GradeAsync(scenario, recording, judged, apiKey, transport),
            _ => throw new InvalidOperationException($"No way to mark a {grade.GetType().Name}.")
        }));

        // A dimension the judge could not answer is left out rather than zeroed: its silence
        // says nothing about the model, and the mean must not move on it.
        return [.. marks.OfType<GradeMark>()];
    }
}