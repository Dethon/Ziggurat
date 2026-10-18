using System.Text.Json;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// A scenario's required call is a claim about what the model must do, so a matcher narrower than
// the claim reports a right answer as a red. Each test here is a command an armed run sent that
// did what the scenario asks.
public class ShellScenariosTests
{
    private static bool Required(Scenario scenario, string label, string command, string path = "/sandbox")
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, command }));
        return scenario.Required.Single(call => call.Label == label).Arguments
            .All(matcher => matcher.Matches(arguments.RootElement));
    }

    // gpt-6-luna, 2026-10-04: silenced from the timers' directory, then listed what was left by
    // reading each timer's status — one command, the claim exactly, with no ls, find or cat in it.
    [Fact]
    public void ADismissThenAReadOfEachTimersStatus_IsTheScript() =>
        Required(ShellScenarios.ADismissInsideAScript, "script",
                "cd /timers && ./dismiss && for f in */status.json; do " +
                "remaining=$(jq -r '.remainingSeconds' \"$f\"); echo \"${f%/status.json} $remaining\"; done")
            .ShouldBeTrue();

    [Fact]
    public void ADismissAlone_IsNotTheScript() =>
        Required(ShellScenarios.ADismissInsideAScript, "script", "/timers/dismiss").ShouldBeFalse();

    // claude-haiku-5.5, 2026-10-10: ran the count from inside the folder, so the folder's name is
    // where the command ran and not in the command — in place on /vault, which is the claim.
    [Fact]
    public void ACountRunFromInsideTheFolder_IsTheCount() =>
        Required(ShellScenarios.WordsAcrossTheVault, "count", "wc -w *.md", path: "/vault/Cocina")
            .ShouldBeTrue();

    [Fact]
    public void ACountNamingTheFolderInTheCommand_IsTheCount() =>
        Required(ShellScenarios.WordsAcrossTheVault, "count", "cat /vault/Cocina/*.md | wc -w", path: "/vault")
            .ShouldBeTrue();

    [Fact]
    public void ACountOfAnotherFolder_IsNotTheCount() =>
        Required(ShellScenarios.WordsAcrossTheVault, "count", "wc -w *.md", path: "/vault/Proyectos")
            .ShouldBeFalse();

    // The copy named by its landed path, converted with ImageMagick.
    [Fact]
    public void AConversionOfTheLandedCopy_IsTheConversion() =>
        Required(ShellScenarios.AnAttachedPhotoIsConvertedInTheSandbox, "convert",
                "convert /sandbox/home/sandbox_user/uploads/eval-1/abc/meal-photo.jpg /sandbox/home/sandbox_user/meal-photo.png && ls -l /sandbox/home/sandbox_user/meal-photo.png")
            .ShouldBeTrue();

    // Run from inside the message's directory, the copy is where the command runs.
    [Fact]
    public void AConversionRunInTheUploadsDirectory_IsTheConversion() =>
        Required(ShellScenarios.AnAttachedPhotoIsConvertedInTheSandbox, "convert",
                "python3 -c \"from PIL import Image; Image.open('foto.jpg').save('foto.png')\" && du -h foto.png",
                path: "/sandbox/home/sandbox_user/uploads/eval-1/abc")
            .ShouldBeTrue();

    // A PNG made from something else is not the conversion.
    [Fact]
    public void APngOfAnotherFile_IsNotTheConversion() =>
        Required(ShellScenarios.AnAttachedPhotoIsConvertedInTheSandbox, "convert",
                "convert /sandbox/home/sandbox_user/other.jpg out.png")
            .ShouldBeFalse();
}