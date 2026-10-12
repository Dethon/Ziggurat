using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelVoice;

// Pins where the running server keeps the config the entrypoint wrote. Since v11.8 lemond keeps its
// JSON state in a config directory of its own, ~/.config/lemonade, and on boot moves a config.json
// it finds in the cache directory over there — once, and only when none is there yet. That directory
// is no mount: the entrypoint's config would be read on a container's first boot and ignored on
// every restart of it, and user_models.json would be lost on a recreate. The entrypoint names the
// mounted directory as lemond's config directory instead, so the file has to still be in it once the
// server answers.
[Trait("Category", "External")]
[Collection(LemonadeCollection.Name)]
public class LemonadeConfigDirTests(LemonadeFixture fixture)
{
    [SkippableFact]
    public void RunningServer_KeepsItsConfigInTheMountedDirectory()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        File.Exists(fixture.ConfigFile).ShouldBeTrue(
            $"lemond moved {fixture.ConfigFile} out of the mount: it is reading its config from a "
            + "directory that does not survive the container");
    }
}