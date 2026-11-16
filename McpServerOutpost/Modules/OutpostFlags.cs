using Domain.DTOs.Tunnel;
using Domain.Outposts;
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
    // command-line configuration provider does not read it that way: a bare switch swallows the
    // next argument as its value (`--jailed --exec` reads Jailed as "--exec") and a bare trailing
    // one is silently dropped. Both booleans are given the value they obviously mean before the
    // provider ever sees them. The host builder's own command-line source still holds the raw
    // args, mis-parsed exactly so — harmless only because this sanitized source sits above it and
    // restates every key a flag can set.
    private static readonly string[] _booleanFlags = ["--jailed", "--exec"];

    // The one value that is not a flag, refused rather than ignored. The reason it is a secret is
    // the reason it cannot be typed: a command line is visible to every process on the machine, so
    // somebody who passed it this way needs to be told it did not work rather than left believing
    // their outpost is gated. The retired shared secret's spellings too: a person typing one is a
    // person about to put a secret on a command line.
    private static readonly string[] _neverFlags =
        ["--credential", "--tunnel-credential", "--secret", "--sharedsecret", "--shared-secret"];

    // What the outpost no longer has, refused with a message rather than quietly ignored: it opens its
    // own tunnel, so there is no address to advertise and no port anything dials.
    private static readonly string[] _retiredFlags = ["--advertise", "--port"];

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
        var configuration = configBuilder.Build();
        var settings = configuration.Get<OutpostSettings>() ?? validated;

        // The tunnel's timings are the shared policy file's (Domain/tunnel.json, which the binder
        // reads), under the section every tunnelled server binds them from.
        return Checked(settings with
        {
            TunnelPolicy = configuration.GetSection("mcp:tunnel").Get<TunnelSettings>() ?? new TunnelSettings()
        });
    }

    internal static string[] Sanitized(string[] args)
    {
        if (args.FirstOrDefault(arg => Names(arg, _neverFlags)) is { } typed)
        {
            throw new InvalidOperationException(
                $"'{typed.Split('=', 2)[0]}' cannot be passed on the command line: a command line is visible "
                + "to every process on this machine, and the tunnel credential is the one value that is a "
                + "secret. Set CREDENTIAL in the environment instead.");
        }

        if (args.FirstOrDefault(arg => Names(arg, _retiredFlags)) is { } retired)
        {
            throw new InvalidOperationException(
                $"'{retired.Split('=', 2)[0]}' is no longer an outpost flag: the outpost opens its own tunnel "
                + "to the hub and listens on nothing the hub dials. Start it again without it.");
        }

        return [.. args.Select(arg => _booleanFlags.Contains(arg, StringComparer.Ordinal) ? arg + "=true" : arg)];
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

    private static bool Names(string arg, string[] flags) =>
        flags.Contains(arg.Split('=', 2)[0], StringComparer.OrdinalIgnoreCase);

    // What the binder cannot say for itself: a name no credential can be minted for, and a credential
    // that was never set — the one value that has to come from the environment.
    private static OutpostSettings Checked(OutpostSettings settings)
    {
        if (!OutpostServerName.IsValidName(settings.Name))
        {
            throw new InvalidOperationException(
                $"'{settings.Name}' cannot be this outpost's --name: its tunnel credential is minted for "
                + $"'{OutpostServerName.For(settings.Name)}', which must be lowercase letters, digits and "
                + "hyphens, at most 63 characters.");
        }

        return settings.Credential.Length > 0
            ? settings
            : throw new InvalidOperationException(
                "This outpost has no tunnel credential. Ask the operator to mint one for "
                + $"'{OutpostServerName.For(settings.Name)}' (mint-tunnel-credential) and set it as CREDENTIAL "
                + "in the environment — never as a flag, because a command line is visible to every "
                + "process on this machine.");
    }
}