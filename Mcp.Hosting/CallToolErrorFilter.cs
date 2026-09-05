using Domain.Channels;
using Domain.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The one error rule every MCP server in the repo answers to: a cancelled call propagates as the
// abort it is; anything else is logged and comes back as the caller's error result. It is also
// where the call's conversation context is entered (CallerContext), so code answering the call
// without the request in hand — a filesystem backend — can still ask who is calling.
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
                using var caller = CallerContext.Enter(ConversationScope.Parse(context.Params?.Meta));
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
                    // A server that passed no error result still answers the envelope. The four
                    // channel servers pass none, and used to hand a model a bare exception message:
                    // no code, no retryability, nothing about what to do next.
                    return errorResult?.Invoke(ex) ?? Envelope(ex);
                }
            }));
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