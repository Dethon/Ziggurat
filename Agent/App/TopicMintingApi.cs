using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;

namespace Agent.App;

// The topic a voice conversation opens under, minted here because the agent owns topics: it writes
// the one the chat client reads into its own store, so a voice server — a household's, at its own
// site — holds no deployment database. Reached on the deployment's own network alone (and, for a
// tunnelled server, as one of its back-calls): the proxy routes /api/agents* and /api/outposts*
// here and nothing under /internal (DeploymentConformanceTests). No secret guards it, and none is
// needed for what it decides: whose topic it mints is whoever the accounts service's assertion for
// a voice turn names — the person, or the household's guest — checked as a turn's is, so a caller
// can mint only for someone the service vouched for to it. The body says what the topic is called
// and nothing about whose it is.
public static class TopicMintingApi
{
    // The same feed entry a turn refused for its assertion makes: the same check failed.
    public const string RefusedErrorType = ChatMonitor.AssertionRefusedErrorType;

    public static void MapTopicMinting(this IEndpointRouteBuilder app) =>
        app.MapPost(TopicMinting.Path, async (
            HttpContext http, TopicMintRequest request, AssertionVerifier assertions,
            IConversationFactory topics, IMetricsPublisher metrics, ILoggerFactory loggers) =>
        {
            var admission = await assertions.VerifyTurnAsync(
                AssertionVerifier.AssertionOf(http.Request.Headers), ChannelIds.Voice, http.RequestAborted);
            if (admission is TurnAdmission.Refused(var reason))
            {
                loggers.CreateLogger(nameof(TopicMintingApi))
                    .LogWarning("Refused to mint a voice topic: its assertion is {Reason}", reason);
                metrics.Publish(new ErrorEvent
                {
                    Service = "agent",
                    ErrorType = RefusedErrorType,
                    Message = $"{ChannelIds.Voice} topic mint: {reason}"
                });
                return Results.Unauthorized();
            }

            var identity = ((TurnAdmission.Admitted)admission).Identity;
            var creation = await topics.CreateAsync(new CreateConversationParams
            {
                AgentId = request.AgentId,
                TopicName = request.TopicName,
                Sender = request.Sender,
                Principal = PrincipalOf(identity)
            }, http.RequestAborted);
            return Results.Json(new TopicMinted(creation.Topic.TopicId, creation.Identity.ConversationId));
        });

    // A person owns the topic their conversation opens; the guest's has no account, so the
    // household shares it.
    private static Principal PrincipalOf(IdentityClaims identity) => identity.IsGuest
        ? new Principal(identity.ChannelId, IdentityClaims.GuestSubject)
        : new Principal(identity.ChannelId, identity.Subject, identity.Account);
}