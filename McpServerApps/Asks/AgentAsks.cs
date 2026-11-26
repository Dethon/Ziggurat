using System.Text.Json;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Tools;
using Mcp.Hosting;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;

namespace McpServerApps.Asks;

// A built app's ask (POST /_ask), already settled as a read is: sent to the app's building agent as a turn
// on this server's own channel, answered by the instructions of the origin's own manifest — the served
// commit's, so an edit in the tree changes nothing a viewer sees until published; the preview the tree's —
// and acting for the viewer on their app session's `apps` assertion, with the tools of that same manifest's
// `asks` servers, on its `model`. A schema the page sends is the turn's response format, and the reply's JSON
// the answer. An ask naming an earlier answer's conversation continues it, if this app and viewer opened it
// within the window (AskConversations), with that ask's own schema or none. Every ask sent counts toward
// the app's daily cap (AskCounts, AskCaps), and one past it is refused before anything is sent. The page
// waits on the whole reply; its giving up, or the wait running out, cancels the turn.
//
// The clock is the wait's alone: the inbox decides whether the agent is listening on the server's own, and
// a test moving this one must not make an agent polling in real time look gone.
public sealed class AgentAsks(
    AgentTurns turns, AskConversations conversations, AskCaps caps, AskCounts counts, TimeProvider clock, AppsSettings settings)
{
    // The longest body an ask of MaxTextLength characters and a schema of MaxSchemaBytes can arrive as: JSON
    // writes a character as six bytes at most (\uXXXX), the schema is measured as written, and the object
    // around them, a conversation id among it, is small.
    private int MaxBodyBytes => settings.Asks.MaxTextLength * 6 + settings.Asks.MaxSchemaBytes + 1024;

    internal async Task<OriginAnswer> AskAsync(
        HttpRequest request, BuiltApp app, AppSession session, AppPages.ServedFiles served, CancellationToken ct)
    {
        if (session.Proof is not ViewerProof.Assertion { Token: var assertion })
        {
            return OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, ToolError.PermissionDenied(
                "A check's page cannot ask its assistant yet, so nothing was asked.",
                "Open the preview yourself to try the app's asks."));
        }

        if (await AppPages.BodyAsync(request, MaxBodyBytes, ct) is not { } body)
        {
            return InvalidAsk($"An ask's body is one JSON object of at most {MaxBodyBytes} bytes.");
        }

        AppAskRequest ask;
        switch (AppAskRequest.Parse(body, settings.Asks.MaxTextLength, settings.Asks.MaxSchemaBytes))
        {
            case AppAskRequestParse.Refused { Problem: var problem }:
                return InvalidAsk(problem);
            case AppAskRequestParse.Valid { Request: var valid }:
                ask = valid;
                break;
            default:
                throw new InvalidOperationException("An ask is either valid or refused.");
        }

        if (served.Manifest.Instructions is not { } path)
        {
            return OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, AppAsks.NoInstructions);
        }

        // Read at each ask: the preview's never went through a publish.
        string instructions;
        switch (AppAsks.ReadInstructions(path, await served.RepositoryFileAsync(path, AppAsks.MaxInstructionsBytes + 1, ct)))
        {
            case AppInstructionsRead.Refused { Problem: var problem }:
                return OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, ToolError.PermissionDenied(
                    $"This app cannot ask its assistant: {problem}", "Fix app.json's instructions and publish."));
            case AppInstructionsRead.Read { Text: var read }:
                instructions = read;
                break;
            default:
                throw new InvalidOperationException("Instructions are either read or refused.");
        }

        // Counted before the conversation is looked at, so an ask past the cap opens none, and the table of
        // them grows no faster than the caps let; given back if the ask goes no further, since only an ask
        // sent is counted.
        AskCount.Counted counted;
        switch (await counts.TryCountAsync(app, await caps.LimitAsync(app, session, ct), ct))
        {
            case AskCount.Capped { Limit: var limit, FreesAt: var freesAt }:
                return OriginAnswer.Json(StatusCodes.Status429TooManyRequests, AppAsks.Capped(limit, freesAt));
            case AskCount.Gone:
                return OriginAnswer.Envelope(StatusCodes.Status404NotFound, ToolError.Result(ToolError.Codes.NotFound,
                    "This app was deleted, so nothing was asked."));
            case AskCount.Counted taken:
                counted = taken;
                break;
            default:
                throw new InvalidOperationException("An ask is counted, capped, or its app gone.");
        }

        var viewer = session.Person;
        // Last before the turn, since continuing a conversation starts its window again.
        string conversation;
        if (ask.Conversation is not { } named)
        {
            conversation = conversations.Open(app.Id, viewer, app.BuildingAgent);
        }
        else if (conversations.TryContinue(app.Id, viewer, app.BuildingAgent, named))
        {
            conversation = named;
        }
        else
        {
            await counts.ReleaseAsync(app, counted);
            return OriginAnswer.Envelope(StatusCodes.Status410Gone, AppAsks.ConversationExpired);
        }

        var reply = await turns.SendAsync(new ChannelMessageNotification
        {
            ConversationId = conversation,
            AgentId = app.BuildingAgent,
            Sender = viewer.UserId,
            // A label only: the agent believes the assertion, renewed with the session if it was due.
            Principal = new Principal(AppChannels.Apps, viewer.UserId, viewer),
            Assertion = assertion,
            Content = ask.Text,
            AppTurn = new AppTurn { AppId = app.Id, Instructions = instructions, Servers = served.Manifest.Asks },
            ResponseFormat = ask.Schema is { } schema ? new ResponseFormat { Name = schema.Name, Schema = schema.Schema } : null,
            ConfigPatch = served.Manifest.Model is { } model ? new AgentConfigPatch { Model = model } : null,
            Timestamp = clock.GetUtcNow()
        }, settings.Asks.Timeout, clock, ct);

        if (reply is TurnReply.NotListening)
        {
            await counts.ReleaseAsync(app, counted);
        }

        return reply switch
        {
            TurnReply.Answered { Text: var answer } when ask.Schema is null =>
                Answered(new AppAskAnswer { Conversation = conversation, Text = answer }),
            TurnReply.Answered { Text: var answer } => AppAsks.ReadReply(answer) switch
            {
                AppAskReplyRead.Parsed { Value: var value } => Answered(new AppAskAnswer { Conversation = conversation, Value = value }),
                // Only the preview's: no one but its author opens it, and the published origin's viewers may be
                // anyone of the household, the reply written for them.
                _ => OriginAnswer.Json(StatusCodes.Status422UnprocessableEntity,
                    AppAsks.InvalidReply(session.Origin.Preview ? answer : null))
            },
            // AgentTurns logged the agent's own words; the page is told only what a refusal says.
            TurnReply.Failed { Reason: var reason } => OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, AppAsks.Refusal(reason)),
            TurnReply.Unanswered => OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, AppAsks.Unanswered),
            TurnReply.NotListening => OriginAnswer.Envelope(StatusCodes.Status503ServiceUnavailable, AppAsks.NotListening),
            TurnReply.TimedOut => OriginAnswer.Envelope(StatusCodes.Status504GatewayTimeout, AppAsks.TimedOut(settings.Asks.Timeout)),
            _ => throw new InvalidOperationException($"A turn's reply is answered, failed, unanswered, unheard or timed out: {reply}.")
        };
    }

    private static OriginAnswer Answered(AppAskAnswer answer) =>
        new(StatusCodes.Status200OK, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(answer, JsonSerializerOptions.Web));

    private static OriginAnswer InvalidAsk(string problem) =>
        OriginAnswer.Envelope(StatusCodes.Status400BadRequest, ToolError.Result(ToolError.Codes.InvalidArgument, problem));
}