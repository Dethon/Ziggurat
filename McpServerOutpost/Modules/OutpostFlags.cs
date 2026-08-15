using Domain.Tools.Files;
using Mcp.Hosting;
using McpServerOutpost.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;

namespace McpServerOutpost.Modules;

// The outpost is configured by flags, because installing it is a copy and starting it is one line.
// Everything else about reading configuration is the shared binder's, so this is only about where
// the flags sit in the stack and how a person is allowed to type them.
public static class OutpostFlags
{
    // --dir is what somebody types; WorkingDirectory is what the settings type calls it. The rest
    // bind by name with no mapping needed, because configuration binding is case-insensitive.
    private static readonly Dictionary<string, string> _switchMappings = new()
    {
        ["--dir"] = "WorkingDirectory"
    };

    // A boolean switch is typed on its own — `--jailed`, not `--jailed true` — and the
    // command-line configuration provider requires every switch to carry a value, throwing on a
    // bare one. Both booleans are given the value they obviously mean before it ever sees them.
    private static readonly string[] _booleanFlags = ["--jailed", "--exec"];

    // The one value that is not a flag, refused rather than ignored. The reason it is a secret is
    // the reason it cannot be typed: a command line is visible to every process on the machine, so
    // somebody who passed it this way needs to be told it did not work rather than left believing
    // their outpost is gated.
    private static readonly string[] _neverFlags = ["--sharedsecret", "--shared-secret", "--secret"];

    public static OutpostSettings GetOutpostSettings(this IConfigurationBuilder configBuilder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(configBuilder);

        var flags = new CommandLineConfigurationSource
        {
            Args = Sanitized(args),
            SwitchMappings = _switchMappings
        };
        configBuilder.Sources.Add(flags);

        // BindSettings appends the environment and user secrets on top of everything the host
        // builder assembled, and the host put the command line underneath both — so a `Jailed`
        // environment variable that happened to be set would beat a --jailed somebody typed.
        // Delegating to the binder keeps its guarantees (one reader, the required-member gate that
        // names what is missing, the user-secret rule); moving the flags above its sources
        // afterwards is what makes a flag the operator typed win.
        var validated = configBuilder.BindSettings<OutpostSettings>();
        configBuilder.Sources.Remove(flags);
        configBuilder.Sources.Add(flags);
        return configBuilder.Build().Get<OutpostSettings>() ?? validated;
    }

    internal static string[] Sanitized(string[] args)
    {
        if (args.FirstOrDefault(Names) is { } typed)
        {
            throw new InvalidOperationException(
                $"'{typed}' cannot be passed on the command line: a command line is visible to every "
                + "process on this machine, and the shared secret is the one value that is a secret. "
                + "Set SHAREDSECRET in the environment instead.");
        }

        return [.. args.Select(arg => _booleanFlags.Contains(arg, StringComparer.Ordinal) ? arg + "=true" : arg)];

        static bool Names(string arg) =>
            _neverFlags.Contains(arg.Split('=', 2)[0], StringComparer.OrdinalIgnoreCase);
    }

    // One machine's list, replacing the shared default wholesale rather than adding to it: a
    // computer full of unusual source files is a different answer to "what is text here", not the
    // usual answer plus a few.
    public static string[] AllowedExtensions(this OutpostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.IsNullOrWhiteSpace(settings.Ext)
            ? TextFileExtensions.Default
            : [.. settings.Ext.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}