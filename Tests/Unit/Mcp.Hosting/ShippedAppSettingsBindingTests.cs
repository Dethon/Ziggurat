using System.Reflection;
using System.Runtime.ExceptionServices;
using global::Mcp.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.Mcp.Hosting;

// The other half of what BindSettings guarantees. SettingsBinderTests proves the required-member
// walk fires on probe types; this proves the thirteen files it fires against are not already
// failing it.
//
// The walk is fail-fast startup behaviour: a required member that binds to null throws before the
// server ever listens. Every other test in the repo hands a server a settings object built in C#,
// so dropping a required key from a shipped appsettings.json stays green everywhere and only shows
// up as a container that crash-loops at boot.
//
// No container, no network and no server: the binder needs an IConfigurationBuilder and a JSON
// file, which is why this sits in Unit despite driving the integration table.
public class ShippedAppSettingsBindingTests
{
    // The internal overload, so the secrets id is a decision this test makes rather than one it
    // inherits from the test host. Passing null switches user secrets off outright, which keeps a
    // developer machine that holds real secrets for these projects from filling in a key the
    // shipped file dropped.
    private static readonly MethodInfo _bindMethod = typeof(SettingsBinder)
        .GetMethod(nameof(SettingsBinder.BindSettings), BindingFlags.Static | BindingFlags.NonPublic)!;

    // Every server that ships an appsettings.json. The outpost ships none and never will: it has no
    // Dockerfile and no compose service either, because it is a file somebody copies onto their own
    // machine and starts with flags. A row with nothing to bind would assert nothing.
    public static TheoryData<string> Servers => McpServerRegistrations.Ids(
        McpServerRegistrations.All.Where(row => File.Exists(ShippedAppSettings(row))));

    // The exemption stated rather than left as an empty theory: a server that quietly stopped
    // shipping its settings file would otherwise drop out of this suite without a word.
    [Fact]
    public void TheOutpostAlone_ShipsNoAppSettings() =>
        McpServerRegistrations.All
            .Where(row => !File.Exists(ShippedAppSettings(row)))
            .Select(row => row.Id)
            .ShouldBe(["outpost"]);

    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_BindsItsShippedAppSettings(string id)
    {
        var row = McpServerRegistrations.Get(id);
        var settingsType = row.Settings.GetType();

        NothingInTheEnvironmentMayShadow(settingsType, id);

        var configBuilder = new ConfigurationBuilder()
            .AddJsonFile(ShippedAppSettings(row), optional: false);

        Bind(configBuilder, settingsType).ShouldNotBeNull();
    }

    // BindSettings always adds environment variables and offers no way to leave them out, so this
    // run is only hermetic while none of them lands on a key the settings type declares. Asserting
    // that rather than assuming it: an ambient value would fill a key the shipped file dropped and
    // hide the one defect this test exists to catch, and a loud failure naming the variable beats a
    // silent pass.
    private static void NothingInTheEnvironmentMayShadow(Type settingsType, string id)
    {
        var declared = settingsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        new ConfigurationBuilder().AddEnvironmentVariables().Build()
            .GetChildren()
            .Select(child => child.Key)
            .Where(declared.Contains)
            .ToList()
            .ShouldBeEmpty($"an environment variable shadows {id}'s settings, so this run cannot tell "
                           + "a shipped key from an ambient one");
    }

    // The settings type comes off the row's own settings object, the same way McpServerContractTests
    // asks the container for it, so the table carries no second statement of it to drift.
    private static object? Bind(IConfigurationBuilder configBuilder, Type settingsType)
    {
        try
        {
            return _bindMethod.MakeGenericMethod(settingsType).Invoke(null, [configBuilder, null]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Otherwise the failure reads as "Exception has been thrown by the target of an
            // invocation" and the message naming the missing key is a level down.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    // The file in the working tree, which the table locates. appsettings.json alone, with no
    // environment overlay, because that is what a container starts from.
    private static string ShippedAppSettings(McpServerRow row) =>
        Path.Combine(McpServerRegistrations.ProjectPath(row), "appsettings.json");
}