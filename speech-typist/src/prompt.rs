//! What goes in a request's `prompt` field.
//!
//! Whisper reads the prompt as text that precedes the audio, so the binding's vocabulary goes
//! first and the previous segment's transcript goes LAST, closest to what is being decoded.
//!
//! whisper.cpp caps the prompt at `n_text_ctx/2` (224 tokens) and keeps the **tail**, which would
//! silently eat the vocabulary on a long continuation. So the cap is applied here instead, and it
//! is the chained text that gets trimmed, from its front and at a word boundary: the vocabulary a
//! person wrote always survives whole. The budget is in characters, a deliberate under-estimate
//! of that token budget rather than a tuning of it.
//!
//! This is the same rule `McpChannelVoice/Services/Stt/WhisperPromptBuilder.cs` follows, and for
//! the same reasons. Neither is derived from the other; both are derived from whisper.

/// The vocabulary, then the tail of what was said last. `None` when there would be nothing in it.
pub fn compose(vocabulary: &str, prior: Option<&str>, max_chars: usize) -> Option<String> {
    let vocabulary = collapse(vocabulary);
    let prior = collapse(prior.unwrap_or_default());

    if vocabulary.is_empty() {
        let tail = tail(&prior, max_chars);
        return (!tail.is_empty()).then_some(tail);
    }

    // The separating space has to come out of the budget too, or a prompt at the limit is one
    // character over it.
    let budget = max_chars.saturating_sub(vocabulary.chars().count() + 1);
    let tail = tail(&prior, budget);
    Some(if tail.is_empty() { vocabulary } else { format!("{vocabulary} {tail}") })
}

/// Keeps the END of the text — the most recent context — and starts it on a whole word, so a
/// fragment never opens mid-syllable and mis-primes the decoder. A word longer than the whole
/// budget is dropped rather than cut: no context primes better than wrong context.
fn tail(text: &str, budget: usize) -> String {
    if budget == 0 {
        return String::new();
    }
    let characters: Vec<char> = text.chars().collect();
    if characters.len() <= budget {
        return text.to_string();
    }

    let from = characters.len() - budget;
    if characters[from - 1] == ' ' {
        return characters[from..].iter().collect();
    }
    match characters[from..].iter().position(|&c| c == ' ') {
        Some(space) => characters[from + space + 1..].iter().collect(),
        None => String::new(),
    }
}

/// Runs of whitespace become one space, so a vocabulary written across several lines does not
/// reach whisper as a shape it has never seen in text.
fn collapse(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    const BUDGET: usize = 700;

    #[test]
    fn the_first_segment_of_a_dictation_carries_the_vocabulary_alone() {
        assert_eq!(
            compose("Ziggurat, Lemonade, nabu", None, BUDGET),
            Some("Ziggurat, Lemonade, nabu".into())
        );
    }

    #[test]
    fn what_was_said_last_goes_after_the_vocabulary_because_it_sits_closest_to_the_audio() {
        let prompt = compose("Ziggurat", Some("estaba diciendo que"), BUDGET).unwrap();

        assert_eq!(prompt, "Ziggurat estaba diciendo que");
    }

    #[test]
    fn with_no_vocabulary_the_prompt_is_what_was_said_last_and_nothing_else() {
        assert_eq!(compose("", Some("estaba diciendo"), BUDGET), Some("estaba diciendo".into()));
    }

    #[test]
    fn with_neither_there_is_no_prompt_at_all() {
        assert_eq!(compose("", None, BUDGET), None);
        assert_eq!(compose("   ", Some("  "), BUDGET), None);
    }

    #[test]
    fn a_long_chain_loses_its_oldest_words_and_never_the_vocabulary() {
        // whisper.cpp keeps the tail of an over-long prompt, so leaving the trimming to it would
        // drop the vocabulary — the one part of the prompt a person actually wrote.
        let vocabulary = "Ziggurat, Lemonade";
        let prior = "palabra ".repeat(200);

        let prompt = compose(vocabulary, Some(&prior), 100).unwrap();

        assert!(prompt.starts_with(vocabulary), "the vocabulary was trimmed: {prompt}");
        assert!(prompt.chars().count() <= 100, "{} chars", prompt.chars().count());
        assert!(prompt.ends_with("palabra"), "the oldest end should be the one dropped");
    }

    #[test]
    fn the_chain_is_cut_at_a_word_boundary_rather_than_mid_syllable() {
        let prompt = compose("", Some("aaaa bbbb cccc dddd"), 11).unwrap();

        assert_eq!(prompt, "cccc dddd");
    }

    #[test]
    fn a_single_word_longer_than_the_budget_is_dropped_rather_than_cut() {
        // No context primes better than wrong context: half a word tells whisper something false.
        let prompt = compose("", Some("supercalifragilisticoespialidoso"), 10);

        assert_eq!(prompt, None);
    }

    #[test]
    fn a_vocabulary_written_across_several_lines_reaches_whisper_as_one() {
        let prompt = compose("Ziggurat,\n  Lemonade,\n  nabu", None, BUDGET).unwrap();

        assert_eq!(prompt, "Ziggurat, Lemonade, nabu");
    }

    #[test]
    fn a_vocabulary_that_fills_the_budget_leaves_no_room_for_the_chain_and_survives_whole() {
        let vocabulary = "x".repeat(100);

        let prompt = compose(&vocabulary, Some("estaba diciendo"), 100).unwrap();

        assert_eq!(prompt, vocabulary);
    }

    #[test]
    fn accented_words_are_measured_in_characters_rather_than_bytes() {
        // "ñ" and "é" are two bytes each; budgeting by bytes would trim a Spanish prompt early
        // and could panic cutting one in half.
        let prompt = compose("", Some("mañana será otro día"), 14).unwrap();

        assert_eq!(prompt, "será otro día");
    }
}
