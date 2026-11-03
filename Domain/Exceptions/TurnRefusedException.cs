namespace Domain.Exceptions;

// A turn that failed for a reason the person can be told in a sentence, one of RefusalAnswers: that
// sentence is the whole reply, never prefixed as an error, so a channel that says refusals in words of
// its own (voice's SpokenRefusals) recognises it.
public sealed class TurnRefusedException(string answer, Exception? inner = null) : Exception(answer, inner);
public static class TurnFailure
{
    // What a person is told when their turn failed: a refusal's own sentence, anything else as the
    // error it was. The reply shows it, and the conversation keeps it in the same words.
    public static string Answer(Exception failure) =>
        failure is TurnRefusedException ? failure.Message : $"An error occurred: {failure.Message}";
}