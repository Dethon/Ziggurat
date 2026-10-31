namespace Domain.DTOs.Metrics.Enums;

// What a household asked of the shared Lemonade, each counted in its own unit
// (LemonadeUsageEvent.Amount): seconds of speech transcribed, characters spoken, texts embedded.
public enum LemonadeUse
{
    Transcription,
    Speech,
    Embedding
}