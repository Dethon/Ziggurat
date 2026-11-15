namespace Domain.Conversations;

// Continue: a turn asking the model to carry on a reply that was cut off. Its message is hidden — never
// drawn, counted or remembered — and the reply it gets reads as the rest of the one it continues.
public static class Continuation
{
    public const string Instruction =
        "Continue your previous reply exactly where it stopped. Do not repeat anything you already said, "
        + "and do not acknowledge this instruction.";
}