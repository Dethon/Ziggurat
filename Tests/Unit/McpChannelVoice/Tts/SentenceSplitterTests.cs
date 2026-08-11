using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class SentenceSplitterTests
{
    [Fact]
    public void TryTake_NoTerminator_TakesNothing()
    {
        SentenceSplitter.TryTake("Mañana por la tarde hará", 10, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_CompleteSentenceOverThreshold_TakesIt()
    {
        SentenceSplitter.TryTake("Hará sol por la tarde. Y algo de", 10, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Hará sol por la tarde.");
        remainder.ShouldBe("Y algo de");
    }

    [Fact]
    public void TryTake_CompleteSentenceUnderThreshold_WaitsForMore()
    {
        // Synthesizing "Sí." on its own costs a whole TTS round trip and lands an audible gap
        // before the rest, so a boundary under the threshold is deliberately not a flush point.
        SentenceSplitter.TryTake("Sí. Ahora mismo", 40, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_SeveralCompleteSentences_TakesThroughTheLastBoundary()
    {
        // Greedy to the last boundary: fewer, larger TTS requests beat many small ones, and
        // cross-sentence prosody survives inside a single request.
        SentenceSplitter.TryTake("Uno. Dos. Tres. Y cua", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Uno. Dos. Tres.");
        remainder.ShouldBe("Y cua");
    }

    [Fact]
    public void TryTake_TerminatorAtEndOfBuffer_WaitsForMore()
    {
        // The buffer's edge is not evidence of a sentence end mid-stream: the next chunk may continue
        // the token ("1." -> "1.234"). StreamComplete flushes the tail, so waiting costs nothing.
        SentenceSplitter.TryTake("Ya está encendida la luz.", 5, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_EnumerationNumber_IsNotABoundary()
    {
        SentenceSplitter.TryTake("Necesitas comprar esto: 1. Leche y 2. Pan ", 5, out _, out _)
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("El total es 1.234,56 euros y algo")]
    public void TryTake_DecimalPoint_IsNotABoundary(string buffer)
    {
        // A '.' between digits never ends a sentence; the whitespace requirement is what excludes it.
        SentenceSplitter.TryTake(buffer, 5, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Viene el Sr. ")]
    [InlineData("Lo trae la Sra. ")]
    [InlineData("Pregunta por el Dr. ")]
    [InlineData("Llegan a las cinco, etc. ")]
    public void TryTake_AbbreviationIsTheLastBoundary_DoesNotFlush(string buffer)
    {
        // The abbreviation rule is only reachable when the abbreviation's dot IS the last boundary
        // in a partially-received buffer — which is exactly what streaming produces. Flushing here
        // would send "…viene el Sr." to TTS on its own and speak the surname in a separate
        // utterance, with an audible seam through the middle of a name.
        SentenceSplitter.TryTake(buffer, 5, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_InitialIsTheLastBoundary_DoesNotFlush()
    {
        SentenceSplitter.TryTake("Te llama J. ", 5, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_AbbreviationBeforeARealBoundary_FlushesThroughTheSentenceEnd()
    {
        // Pins the greedy last-boundary selection: the abbreviation is passed over rather than
        // splitting there.
        SentenceSplitter.TryTake("Viene el Sr. García a las cinco. ", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Viene el Sr. García a las cinco.");
        remainder.ShouldBe("");
    }

    [Fact]
    public void TryTake_SpanishQuestion_SplitsAfterTheClosingMark()
    {
        SentenceSplitter.TryTake("¿Quieres que la apague? Dime cuán", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("¿Quieres que la apague?");
        remainder.ShouldBe("Dime cuán");
    }

    [Fact]
    public void TryTake_Ellipsis_SplitsAfterTheWholeRun()
    {
        SentenceSplitter.TryTake("Espera... ya voy", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Espera...");
        remainder.ShouldBe("ya voy");
    }

    [Fact]
    public void TryTake_OnlyWhitespaceAfterTerminator_LeavesEmptyRemainder()
    {
        SentenceSplitter.TryTake("Hecho.   ", 5, out var speakable, out var remainder).ShouldBeTrue();

        speakable.ShouldBe("Hecho.");
        remainder.ShouldBe("");
    }
}