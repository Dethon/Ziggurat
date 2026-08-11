using McpChannelVoice.Services.Verification;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class EmbeddingMathTests
{
    [Fact]
    public void L2Normalize_ScalesToUnitLength()
    {
        var v = OnnxSpeakerEmbedder.L2Normalize([3f, 4f]);
        v[0].ShouldBe(0.6f, 1e-5f);
        v[1].ShouldBe(0.8f, 1e-5f);
    }

    [Fact]
    public void L2Normalize_ZeroVector_ReturnsZeroVector()
    {
        OnnxSpeakerEmbedder.L2Normalize([0f, 0f]).ShouldBe([0f, 0f]);
    }

    [Fact]
    public void Cosine_IdenticalNormalizedVectors_IsOne()
    {
        var v = OnnxSpeakerEmbedder.L2Normalize([1f, 2f, 3f]);
        OnnxSpeakerEmbedder.Cosine(v, v).ShouldBe(1.0, 1e-5);
    }
}