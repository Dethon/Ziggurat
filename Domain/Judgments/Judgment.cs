namespace Domain.Judgments;

// What one call answered: an answer per question id, the model that answered, and what it cost.
public sealed record Judgment(
    string Model,
    IReadOnlyDictionary<string, JudgmentAnswer> Answers,
    JudgmentUsage Usage);

public abstract record JudgmentAnswer;

public sealed record ChoiceAnswer(
    string Choice,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities) : JudgmentAnswer;

public sealed record NoulAnswer(double Probability) : JudgmentAnswer;

// The cost is the provider's own figure for this call, null where a judge reports none.
public sealed record JudgmentUsage(int InputTokens, int OutputTokens, decimal? Cost = null);