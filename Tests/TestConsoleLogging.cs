using System.Runtime.CompilerServices;

namespace Tests;

// The suite starts dozens of servers in process, each with the framework's console logger at
// Information, so every SQL statement EF ran, every host start and stop, and every unencrypted
// data-protection key in a temp folder went to the test process's console: about seventeen thousand
// lines in one run of the Sugary and accounts tests. The console provider alone is lowered to
// warnings, through the environment every host's configuration reads, so a logger a test captures
// to assert on still sees everything. Two warnings every test host gives are dropped too: keys a host
// keeps unencrypted in a temp folder, and the wwwroot a server's test bin has no copy of.
//
// A variable already set is left alone: `Logging__Console__LogLevel__Default=Information dotnet test`
// puts it all back for a run that needs it.
internal static class TestConsoleLogging
{
    private static readonly (string Category, string Level)[] _console =
    [
        ("Default", "Warning"),
        ("Microsoft.AspNetCore.DataProtection", "Error"),
        ("Microsoft.AspNetCore.StaticFiles", "Error")
    ];

    [ModuleInitializer]
    internal static void LowerTheConsole()
    {
        foreach (var name in _console
                     .Select(rule => (Name: $"Logging__Console__LogLevel__{rule.Category}", rule.Level))
                     .Where(rule => Environment.GetEnvironmentVariable(rule.Name) is null))
        {
            Environment.SetEnvironmentVariable(name.Name, name.Level);
        }
    }
}