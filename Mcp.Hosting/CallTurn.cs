using Domain.Channels;
using Domain.DTOs.Channel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The one place a server reads who a call acts for. Today that is the turn the agent stamps in the
// call's _meta, which nothing verifies; answering the verified call token instead is a change to
// this file alone, which only holds while no tool reads the _meta around it
// (CallTurnConformanceTests). Null means the call carried no turn, and the caller refuses rather
// than guesses.
public static class CallTurn
{
    extension(RequestContext<CallToolRequestParams> call)
    {
        public ConversationContext? Turn => ConversationScope.Parse(call.Params?.Meta);

        // The namespace a stateful tool caches the turn's state under (ConversationScope).
        public bool TryScope(out string scope)
        {
            if (call.Turn is not { } turn)
            {
                scope = string.Empty;
                return false;
            }

            scope = ConversationScope.Build(turn.AgentId, turn.ConversationId);
            return true;
        }
    }
}