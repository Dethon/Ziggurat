using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Security;
using Domain.Security.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The second check on every server's /mcp, after the credential: a tool call must carry the agent's
// call token for this server, in date, signed with a key the agent publishes. Holding a server's
// credential lets a caller talk to it; only the agent's signature says who a call acts for, so a
// container that holds the credential and writes somebody into _meta acts for nobody — it is refused.
//
// Tool calls alone, because a tool call is the one message anything can act for a person on:
// CallTurn, the only way a server learns who a call is for, exists for tools/call and nothing else.
// The handshake, the tool and prompt lists and resource reads are what the agent asks while it
// builds a session, before any turn and for nobody, and every server answers them the same to
// everyone the credential admits.
//
// A message filter rather than a call-tool filter, so the refusal reaches the caller as the
// protocol error it is — the error filter never turns it into a tool's result — and so a server
// keeps exactly one call-tool filter. What it verified is left on the message's own Items, which the
// tool's RequestContext shares: per-call state the call carries, never an ambient.
//
// Checked once, when the call arrives: a command that runs for an hour, a long poll or an approval
// waiting on a person runs to its end on the token it arrived with.
internal sealed class CallTokenGate
{
    public const string ErrorType = "CallTokenRefused";

    // A token's dates are the wall clock's on both ends, so the gate reads the wall clock — never the
    // TimeProvider a host registers for its own work, which a harness pins to whatever day its
    // scenario is set on (an eval pins some servers and not others, and the agent signs on the wall
    // clock). A test that walks a turn past a token's lifetime hands the gate its clock under this key.
    internal const string ClockKey = "call-tokens";

    private readonly TokenVerifier _verifier;
    private readonly ILogger<CallTokenGate>? _logger;

    public CallTokenGate(McpGateSettings gate, IServiceProvider services)
    {
        Audience = gate.Audience;
        var clock = services.GetKeyedService<TimeProvider>(ClockKey) ?? TimeProvider.System;
        var http = services.GetRequiredService<IHttpClientFactory>();
        var url = gate.AgentKeysUrl;
        _logger = services.GetService<ILogger<CallTokenGate>>();
        // A client per fetch from the factory, so a singleton never pins one past its handler's life.
        var keys = new JwksCache(
            ct => url.Length == 0
                ? throw new InvalidOperationException("MCP__AGENTKEYSURL is not set, so the agent's keys cannot be fetched")
                : http.CreateClient(nameof(CallTokenGate)).GetStringAsync(url, ct),
            clock,
            gate.TokenVerification,
            services.GetService<ILogger<JwksCache>>());
        _verifier = new TokenVerifier(TokenIssuers.Agent, keys, clock, gate.TokenVerification);
    }

    // This server's name as the agent dials it, which a token must be signed for.
    public string Audience { get; }

    internal static IMcpServerBuilder Install(IMcpServerBuilder builder, McpGateSettings gate)
    {
        builder.Services
            .AddHttpClient()
            .AddSingleton(services => new CallTokenGate(gate, services));

        return builder.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, ct) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
            {
                var services = context.Services ?? context.Server.Services!;
                context.Items[CallTurn.IdentityKey] =
                    await services.GetRequiredService<CallTokenGate>().AdmitAsync(request, services, ct);
            }

            await next(context, ct);
        }));
    }

    private async Task<IdentityClaims> AdmitAsync(JsonRpcRequest request, IServiceProvider services, CancellationToken ct)
    {
        var verification = await _verifier.VerifyAsync(TokenOf(request), Audience, ct);
        if (verification.IsVerified)
        {
            return verification.Claims;
        }

        var tool = CallText.StringAt(request.Params as JsonObject, "name") ?? "a tool";
        var refusal = verification.Refusal.Value;
        _logger?.LogWarning("Refused a call to {Tool}: its call token is {Refusal}", tool, refusal);
        services.GetService<IMetricsPublisher>()?.Publish(new ErrorEvent
        {
            Service = Audience.Length > 0 ? Audience : "mcp",
            ErrorType = ErrorType,
            Message = $"{tool}: {refusal}"
        });
        throw new McpProtocolException($"Refused ({refusal}): {Explain(refusal)}", McpErrorCode.InvalidRequest);
    }

    // Whatever shape a caller sent: anything that is not a string where the token goes is no token.
    private static string? TokenOf(JsonRpcRequest request) =>
        CallText.StringAt((request.Params as JsonObject)?["_meta"] as JsonObject, ChannelProtocol.CallTokenMetaKey);

    private static string Explain(TokenRefusal refusal) => refusal switch
    {
        TokenRefusal.Missing => "the call carries no call token; only the agent's signed calls are taken",
        TokenRefusal.Expired => "the call token has expired",
        TokenRefusal.NotYetValid => "the call token is not valid yet",
        TokenRefusal.WrongAudience => "the call token was signed for another server",
        TokenRefusal.WrongIssuer => "the call token was not issued by the agent",
        TokenRefusal.UnknownKey => "the call token was signed with a key the agent does not publish",
        TokenRefusal.BadSignature => "the call token's signature does not verify",
        TokenRefusal.KeysUnavailable => "the agent's published keys could not be fetched to check the call token",
        _ => "the call token is malformed"
    };
}