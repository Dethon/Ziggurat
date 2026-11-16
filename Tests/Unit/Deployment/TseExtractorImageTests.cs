using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Deployment;

// torch 2.1.2 is built against NumPy 1.x: under NumPy 2 every tensor<->array hand-off raises
// "Numpy is not available", and torchaudio.load is one, so every /extract answers 500. A pin on the
// layer that names numpy does not hold the layers after it: onnx pulled ml_dtypes 0.6, which wants
// numpy>=2, and pip upgraded numpy under torch (prod, 2026-11-12). So the pin is a constraint every
// pip install after torch's reads.
public class TseExtractorImageTests
{
    private static readonly string Dockerfile = File.ReadAllText(
        Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "tse-extractor", "Dockerfile"));

    [Fact]
    public void EveryPipInstallAfterTorch_IsConstrainedToNumPyOne()
    {
        var lines = Dockerfile.Split('\n');
        var constraint = Array.FindIndex(lines, l => l.StartsWith("ENV PIP_CONSTRAINT=", StringComparison.Ordinal));
        var torch = Array.FindIndex(lines, l => l.Contains("\"torch==2.1.2\"", StringComparison.Ordinal));
        var installs = lines.Select((l, i) => (l, i))
            .Where(x => x.l.StartsWith("RUN pip install", StringComparison.Ordinal) && x.i != torch)
            .Select(x => x.i)
            .ToList();

        constraint.ShouldBeGreaterThan(-1, "the image sets no pip constraint");
        installs.ShouldNotBeEmpty();
        installs.ShouldAllBe(i => i > constraint);
        Dockerfile.ShouldContain("numpy<2");
        var file = lines[constraint]["ENV PIP_CONSTRAINT=".Length..].Trim();
        lines.Take(constraint).Any(l => l.Contains("numpy<2", StringComparison.Ordinal) && l.Contains(file, StringComparison.Ordinal))
            .ShouldBeTrue($"nothing writes numpy<2 into {file}");
    }
}