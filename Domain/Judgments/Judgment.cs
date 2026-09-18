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

public sealed record JudgmentUsage(int InputTokens, int OutputTokens);