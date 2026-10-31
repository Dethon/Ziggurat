using System.CommandLine;
using Agent.Settings;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;

namespace Agent.Commands;

// The agent's verbs besides serving, run inside its container beside the running agent, over the
// same key volume and the same channel endpoints:
//
//   docker exec agent dotnet Agent.dll rotate-signing-key
//   docker exec agent dotnet Agent.dll redelegate [--yes]
//   docker exec agent dotnet Agent.dll retire-signing-keys [--force]
//
// The agent signs call tokens and the delegations deferred turns act on with one key. A rotation
// makes a new key the signing one and keeps the previous published, which the running agent and
// every server notice on their own. Call tokens live two minutes; delegations live as long as their
// objects, so retiring a key would leave every schedule and watch it signed acting for nobody —
// redelegate signs them again with the current key first, and retirement refuses while any is left.
// After the key is lost (the volume gone), the agent generates a new one on start and every stored
// delegation stops verifying: redelegate shows each as it claims to be and signs it again on the
// operator's yes.
public static class AgentCommand
{
    public const string RotateSigningKey = "rotate-signing-key";
    public const string RetireSigningKeys = "retire-signing-keys";
    public const string Redelegate = "redelegate";

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args is [RotateSigningKey or RetireSigningKeys or Redelegate, ..];

    // The clock is the wall clock the running agent signs on, handed in by the host: a command signs
    // and verifies the same tokens the agent does.
    public static Task<int> RunAsync(
        string[] args, AgentSettings settings, TimeProvider clock, TextReader input, TextWriter output,
        CancellationToken ct)
    {
        var yes = new Option<bool>("--yes")
        {
            Description = "Re-delegate every delegation the agent cannot believe as it claims to be, without asking"
        };
        var force = new Option<bool>("--force")
        {
            Description = "Retire even while stored delegations are signed with a key it unpublishes, or a server cannot be asked"
        };

        var rotate = new Command(RotateSigningKey, "Sign with a new key from now on, keeping the previous one published");
        rotate.SetAction((_, _) => RotateAsync(settings, output));

        var redelegate = new Command(Redelegate, "Sign every stored delegation again with the current key") { yes };
        redelegate.SetAction((parse, token) => WithKeysAsync(settings, clock, output, (keys, tokens) =>
            Redelegation(keys, tokens, settings, clock, input, output)
                .RedelegateAsync(Servers(settings, tokens), parse.GetValue(yes), token)));

        var retire = new Command(RetireSigningKeys, "Unpublish every key but the current one") { force };
        retire.SetAction((parse, token) => RetireAsync(settings, clock, parse.GetValue(force), input, output, token));

        var root = new RootCommand("The agent's operator commands") { rotate, redelegate, retire };
        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, ct);
    }

    private static async Task<int> RotateAsync(AgentSettings settings, TextWriter output)
    {
        if (await ProvidedKeyAsync(settings, output))
        {
            return 1;
        }

        var keys = SigningKeyStore.Open(settings.Signing);
        var before = keys.PublishedKeys.Keys.Select(k => k.KeyId).ToList();
        keys.Rotate();
        await output.WriteLineAsync(
            $"The agent now signs with key {SigningKeyStore.Open(settings.Signing).CurrentKeyId}. Still published: {string.Join(", ", before)}. "
            + $"Run {Redelegate} to sign every stored delegation again with it, then {RetireSigningKeys} once the last call token "
            + $"the old key signed has run out, {RetireAfter(settings).TotalMinutes:0.#} minutes from now.");
        return 0;
    }

    private static Task<int> RetireAsync(
        AgentSettings settings, TimeProvider clock, bool force, TextReader input, TextWriter output, CancellationToken ct) =>
        WithKeysAsync(settings, clock, output, async (keys, tokens) =>
        {
            var stranded = await Redelegation(keys, tokens, settings, clock, input, output)
                .StrandedByRetiringAsync(Servers(settings, tokens), ct);
            if (!force && stranded is not 0)
            {
                await output.WriteLineAsync(stranded is null
                    ? "Nothing was retired: a server that may keep delegations could not be asked. Run it again once it answers, or with --force."
                    : $"Nothing was retired: {stranded} stored delegation(s) are still signed with a key it would unpublish. Run {Redelegate} first.");
                return 1;
            }

            var before = keys.PublishedKeys.Keys.Select(k => k.KeyId).ToList();
            keys.Retire();
            await output.WriteLineAsync($"Published now: {keys.CurrentKeyId} alone. Retired: "
                                        + string.Join(", ", before.Where(id => id != keys.CurrentKeyId).DefaultIfEmpty("none")) + ".");
            return 0;
        });

    private static async Task<int> WithKeysAsync(
        AgentSettings settings, TimeProvider clock, TextWriter output, Func<SigningKeyStore, CallTokens, Task<int>> run)
    {
        if (await ProvidedKeyAsync(settings, output))
        {
            return 1;
        }

        var keys = SigningKeyStore.Open(settings.Signing);
        return await run(keys, new CallTokens(keys, clock, settings.CallTokens, settings.Delegations));
    }

    private static Redelegation Redelegation(
        SigningKeyStore keys, CallTokens tokens, AgentSettings settings, TimeProvider clock, TextReader input,
        TextWriter output) =>
        new(keys, tokens, clock, settings.Mcp.TokenVerification, input, output);

    // A test stack's agent signs with the one key it is handed, which is neither rotated nor retired,
    // and its delegations never need signing again.
    private static async Task<bool> ProvidedKeyAsync(AgentSettings settings, TextWriter output)
    {
        if (settings.Signing.PrivateKey.Length == 0)
        {
            return false;
        }

        await output.WriteLineAsync(
            "Nothing was changed: the agent signs with the one key SIGNING__PRIVATEKEY provides, which is neither rotated nor retired here.");
        return true;
    }

    // Every channel endpoint, dialled the way the agent dials it: the dual-role servers among them keep
    // the schedules and watches, and the rest answer that they keep nothing.
    private static IReadOnlyList<Redelegation.Server> Servers(AgentSettings settings, CallTokens tokens)
    {
        var credentials = McpCredentials.From(settings.Mcp);
        return
        [
            .. settings.ChannelEndpoints.Select(endpoint => new Redelegation.Server(endpoint.ChannelId, ct =>
                McpClient.CreateAsync(new SigningClientTransport(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint.Endpoint),
                    AdditionalHeaders = credentials.For(endpoint.Endpoint) is { } secret
                        ? new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
                        : null
                }), tokens, TokenAudiences.CallTo(endpoint.Endpoint)), cancellationToken: ct)))
        ];
    }

    // The last call token the old key signed has run out, with the skew every verifier forgives.
    private static TimeSpan RetireAfter(AgentSettings settings) =>
        settings.CallTokens.Lifetime + settings.Mcp.TokenVerification.ClockSkew;
}