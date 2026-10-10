using Domain.Tools;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

// What a tool server says a call to one of its tools touches. Its own footprint where it
// declared one (FootprintTemplate); otherwise the protocol's read-only hint, read as a tool that
// only reads its own server's state — it runs beside anything but a tool that said nothing.
// A tool that declared neither answers null and is ordered against everything.
internal static class McpFootprint
{
    public static FootprintDeclaration? Of(string serverName, Tool tool) =>
        FootprintTemplate.Parse(tool.Meta?[FootprintTemplate.MetaKey])
        ?? (tool.Annotations?.ReadOnlyHint == true
            ? FootprintDeclaration.Always(CallFootprint.Reading(SharedResource.Of("mcp", serverName)))
            : null);
}