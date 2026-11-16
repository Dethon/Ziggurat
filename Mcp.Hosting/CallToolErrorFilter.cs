using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Metrics;
using Domain.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The one error rule every MCP server in the repo answers to: a cancelled call propagates as the
// abort it is; anything else is logged and comes back as the caller's error result.
//
// Cancellation is carved out because a cancelled call is a call somebody hung up on —
// channel_receive's long poll whenever the agent disconnects or the server shuts down, an fs_exec or
// a web fetch whenever the agent abandons the turn. Mapping that to IsError hands the caller's pump
// something to retry, and the work was deliberately stopped.
//
// Installed at most once. A dual-role server asks for it as a tool server and again as a channel
// server; two filters nested around each other would let the outer one convert the very
// cancellation the inner deliberately rethrows. The first ask wins, so the error shape is the one
// the first caller passed.
internal static class CallToolErrorFilter
{
    public const string ToolCallFailedErrorType = "ToolCallFailed";

    internal static IMcpServerBuilder AddCallToolErrorFilter(
        this IMcpServerBuilder builder,
        Func<Exception, CallToolResult>? errorResult)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(Installed)))
        {
            return builder;
        }

        builder.Services.AddSingleton<Installed>();

        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(
            next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                // Only the caller's own token tripping is a hang-up. An HttpClient timeout throws
                // TaskCanceledException with the same shape and no caller behind it; that is an
                // ordinary failure and falls through to the logged error-result path below.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    context.Services?.GetService<ILoggerFactory>()
                        ?.CreateLogger(typeof(CallToolErrorFilter))
                        .LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    Report(context, ex);
                    // A server that passed no error result still answers the envelope. The four
                    // channel servers pass none, and used to hand a model a bare exception message:
                    // no code, no retryability, nothing about what to do next.
                    return errorResult?.Invoke(ex) ?? Envelope(ex);
                }
            }));
    }

    // A shared server answers every household, so its failure is counted under the household the
    // verified call acted for; a call for nobody is the deployment's own. Only the gate's verified
    // identity is read: a household written into the _meta would bill another's feed. A host that
    // publishes no metrics reports nothing, and an own server's report is restamped by its relay.
    private static void Report(RequestContext<CallToolRequestParams> context, Exception ex)
    {
        if (context.Services?.GetService<IMetricsPublisher>() is not { } metrics)
        {
            return;
        }

        var household = context.Identity is { IsNobody: false, TenantId.Length: > 0 } identity ? identity.TenantId : null;
        var audience = context.Services.GetService<CallTokenGate>()?.Audience;
        metrics.ForHousehold(household).Publish(new ErrorEvent
        {
            Service = audience is { Length: > 0 } ? audience : "mcp",
            ErrorType = ToolCallFailedErrorType,
            Message = $"{context.Params?.Name ?? "a tool"}: {ex.Message}"
        });
    }

    private static CallToolResult Envelope(Exception ex) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = ToolError.Create(ToolError.CodeFor(ex), ex.Message).ToJsonString()
            }
        ]
    };

    // Marks the filter as installed. Never resolved — its presence in the collection is the whole
    // signal, which is what makes a second ask a no-op rather than a second filter.
    private sealed class Installed;
}