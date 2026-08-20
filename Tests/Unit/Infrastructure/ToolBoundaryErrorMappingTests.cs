using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Exceptions;
using Domain.Tools;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The tools that still throw reach a model through one mapping, and it is the only chance those
// failures get to be told apart. A rejected credential answered as a malformed argument sends the
// agent rewriting arguments that were right; a refusal answered as a passing outage sends it round
// the same call until the turn runs out.
public class ToolBoundaryErrorMappingTests
{
    public static TheoryData<Exception, string> Mappings => new()
    {
        { new ArgumentException("bad"), ToolError.Codes.InvalidArgument },
        { new NotSupportedException("no"), ToolError.Codes.UnsupportedOperation },
        { new UnauthorizedAccessException("denied"), ToolError.Codes.PermissionDenied },
        { new TimeoutException("slow"), ToolError.Codes.Timeout },
        { new OperationCanceledException("gone"), ToolError.Codes.Timeout },
        { new SocketException(), ToolError.Codes.TransientDependency },
        { new IOException("disk"), ToolError.Codes.TransientDependency },
        { new HomeAssistantNotFoundException("entity"), ToolError.Codes.NotFound },
        { new HomeAssistantUnauthorizedException("token"), ToolError.Codes.Authentication },
        { new HomeAssistantException("forbidden", 403), ToolError.Codes.PermissionDenied },
        { new HomeAssistantException("slow down", 429), ToolError.Codes.RateLimited },
        { new HomeAssistantException("bad shape", 400), ToolError.Codes.InvalidArgument },
        { new HomeAssistantException("integration crashed", 500), ToolError.Codes.TransientDependency },
        { new HomeAssistantException("never arrived"), ToolError.Codes.TransientDependency },
        { Http(HttpStatusCode.Unauthorized), ToolError.Codes.Authentication },
        { Http(HttpStatusCode.Forbidden), ToolError.Codes.PermissionDenied },
        { Http(HttpStatusCode.NotFound), ToolError.Codes.NotFound },
        { Http(HttpStatusCode.RequestTimeout), ToolError.Codes.Timeout },
        { Http(HttpStatusCode.Conflict), ToolError.Codes.AlreadyExists },
        { Http(HttpStatusCode.TooManyRequests), ToolError.Codes.RateLimited },
        { Http(HttpStatusCode.BadRequest), ToolError.Codes.InvalidArgument },
        { Http(HttpStatusCode.BadGateway), ToolError.Codes.TransientDependency },
        { new HttpRequestException("no route to host"), ToolError.Codes.TransientDependency },
        { new InvalidOperationException("something else"), ToolError.Codes.InternalError }
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Create_AThrownFailure_AnswersTheCodeACallerWouldActOn(Exception thrown, string code)
    {
        Envelope(ToolResponse.Create(thrown))["errorCode"]!.GetValue<string>().ShouldBe(code);
    }

    // The mapping picks the code and nothing else: whether that code is worth retrying is the
    // taxonomy's answer, so this boundary and every tool raising the same code cannot disagree.
    [Theory]
    [MemberData(nameof(Mappings))]
    public void Create_Retryability_ComesFromTheCodeRatherThanFromTheException(Exception thrown, string code)
    {
        Envelope(ToolResponse.Create(thrown))["retryable"]!.GetValue<bool>()
            .ShouldBe(ToolError.IsRetryable(code));
    }

    [Fact]
    public void Create_AnythingThrown_IsAnErrorAtTheProtocolLevelToo()
    {
        ToolResponse.Create(new TimeoutException("slow")).IsError.ShouldBe(true);
    }

    private static HttpRequestException Http(HttpStatusCode status) =>
        new("upstream said no", null, status);

    private static JsonObject Envelope(CallToolResult result) =>
        JsonNode.Parse(result.Content[0].ShouldBeOfType<TextContentBlock>().Text)!.AsObject();
}