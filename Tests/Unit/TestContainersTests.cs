using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;

namespace Tests.Unit;

// Docker Desktop groups whatever carries a compose project label under that project's name, so the
// suite's containers only stay together while every fixture asks for its builder the same way. A
// raw `new ContainerBuilder(...)` anywhere still works and still passes its own test — it just
// lands loose in the container list next to whatever else is running on the machine.
public class TestContainersTests
{
    [Fact]
    public void Container_BuiltThroughTheHelper_CarriesTheZigguratTestsProjectLabel()
    {
        var builder = TestContainers.Container("redis:7-alpine");

        LabelsOf(builder).ShouldContainKeyAndValue(TestContainers.ProjectLabel, TestContainers.ProjectName);
    }

    [Fact]
    public void Network_BuiltThroughTheHelper_CarriesTheZigguratTestsProjectLabel()
    {
        var builder = TestContainers.Network();

        LabelsOf(builder).ShouldContainKeyAndValue(TestContainers.ProjectLabel, TestContainers.ProjectName);
    }

    [Fact]
    public void EveryFixture_AsksForItsContainersThroughTheHelper()
    {
        var offenders = SuiteSources()
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (line, number: index + 1))
                .Where(l => Regex.IsMatch(l.line, @"new (Container|Network)Builder\b"))
                .Select(l => $"{Path.GetFileName(file)}:{l.number}"))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Use TestContainers.Container/Network so the containers group under {TestContainers.ProjectName}.");
    }

    private static IEnumerable<string> SuiteSources()
    {
        var root = Path.GetDirectoryName(typeof(TestContainersTests).Assembly.Location)!;
        var tests = new DirectoryInfo(root);
        while (tests is not null && tests.Name != "Tests")
        {
            tests = tests.Parent;
        }

        tests.ShouldNotBeNull();
        return Directory.EnumerateFiles(tests.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != $"{nameof(TestContainers)}.cs"
                        && Path.GetFileName(f) != $"{nameof(TestContainersTests)}.cs");
    }

    private static IEnumerable<Type> Hierarchy(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            yield return t;
        }
    }

    // The builders keep their configuration on a protected member; reading it back is the only way
    // to assert the label without starting a container.
    private static IReadOnlyDictionary<string, string> LabelsOf(object builder)
    {
        // Declared on both the builder and its base with different return types, so ask type by
        // type from the most derived and take the first.
        var property = Hierarchy(builder.GetType())
            .Select(t => t.GetProperty(
                "DockerResourceConfiguration",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .FirstOrDefault(p => p is not null)
            ?? throw new InvalidOperationException($"{builder.GetType().Name} exposes no configuration.");

        var configuration = property.GetValue(builder)!;
        return (IReadOnlyDictionary<string, string>)configuration.GetType()
            .GetProperty("Labels")!
            .GetValue(configuration)!;
    }
}