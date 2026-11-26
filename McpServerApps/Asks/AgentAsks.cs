using System.Text.Json;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Tools;
using Mcp.Hosting;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace McpServerApps.Asks;

// A built app's ask (POST /_ask), already settled as a read is: sent to the app's building agent as a turn
// on this server's own channel, answered by the instructions of the origin's own manifest — the served
// commit's, so an edit in the tree changes nothing a viewer sees until published; the preview the tree's —
// and acting for the viewer on their app session's `apps` assertion (a check's page: on the call token of the
// call that ran it, as its person), with the tools of that same manifest's `asks` servers, on its `model`. A schema the page sends is the turn's response format, and the reply's JSON
// the answer. An ask naming an earlier answer's conversation continues it, if this app and viewer opened it
// within the window (AskConversations), with that ask's own schema or none. Every ask sent counts toward
// the app's daily cap (AskCounts, AskCaps), and one past it is refused before anything is sent. The page
// waits on the whole reply; its giving up, or the wait running out, cancels the turn.
//
// The clock is the wait's alone: the inbox decides whether the agent is listening on the server's own, and
// a test moving this one must not make an agent polling in real time look gone.
public sealed class AgentAsks(
    AgentTurns turns, AskConversations conversations, AskCaps caps, AskCounts counts, UploadStore uploads,
    IHouseholdConfiguration households, TimeProvider clock, AppsSettings settings)
{
    // What a form's part carries besides its bytes (its boundary and headers), generously.
    private const int PartOverhead = 1024;

    // The longest body an ask of MaxTextLength characters and a schema of MaxSchemaBytes can arrive as: JSON
    // writes a character as six bytes at most (\uXXXX), the schema is measured as written, and the object
    // around them, a conversation id among it, is small.
    private int MaxBodyBytes => settings.Asks.MaxTextLength * 6 + settings.Asks.MaxSchemaBytes + 1024;

    // The longest form an ask with images can arrive as: that JSON, and each image at its bound.
    private long MaxFormBytes =>
        MaxBodyBytes + PartOverhead + settings.Asks.MaxImages * (settings.Asks.MaxImageBytes + PartOverhead);

    internal async Task<OriginAnswer> AskAsync(
        HttpRequest request, BuiltApp app, AppSession session, AppPages.ServedFiles served, CancellationToken ct)
    {
        // Who the agent is to believe it is for, and the household its cap is read on: the viewer's own `apps`
        // assertion; or a check's call token, never both, and its wait never past the check's end, when the
        // token is good no longer.
        var (assertion, checkCall, household, wait) = session.Proof switch
        {
            ViewerProof.Assertion { Token: var token } =>
                (token, null, CallHousehold.OfPerson(app.HouseholdId, token, households), settings.Asks.Timeout),
            ViewerProof.CheckCall { Token: var token, Household: var proved } =>
                ((string?)null, token, (ICallHousehold)proved, Min(settings.Asks.Timeout, session.ExpiresAt - clock.GetUtcNow())),
            _ => throw new InvalidOperationException("A session is proved by an assertion or a check's call token.")
        };

        byte[] body;
        IReadOnlyList<AppAskUpload> sent;
        switch (await ReadAsync(request, ct))
        {
            case AskBody.Refused { Problem: var problem }:
                return InvalidAsk(problem);
            case AskBody.Read read:
                (body, sent) = (read.Json, read.Uploads);
                break;
            default:
                throw new InvalidOperationException("An ask's body is either read or refused.");
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

        IReadOnlyList<AppAskImage> images;
        switch (AppAskImages.Read(sent, settings.Asks.MaxImages, settings.Asks.MaxImageBytes, settings.Asks.ImageTypes))
        {
            case AppAskImagesRead.Refused { Problem: var problem }:
                return InvalidAsk(problem);
            case AppAskImagesRead.Valid { Images: var valid }:
                images = valid;
                break;
            default:
                throw new InvalidOperationException("An ask's images are either valid or refused.");
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
        int limit;
        switch (await caps.LimitAsync(app, household, ct))
        {
            case AskLimit.Read read:
                limit = read.Limit;
                break;
            case AskLimit.Unreadable:
                return OriginAnswer.Envelope(StatusCodes.Status503ServiceUnavailable, AppAsks.CapUnreadable);
            case AskLimit.Refused:
                return OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, AppAsks.CapRefused);
            default:
                throw new InvalidOperationException("An app's limit is read, unreadable or refused.");
        }

        AskCount.Counted counted;
        switch (await counts.TryCountAsync(app, limit, ct))
        {
            case AskCount.Capped { Limit: var reached, FreesAt: var freesAt }:
                return OriginAnswer.Json(StatusCodes.Status429TooManyRequests, AppAsks.Capped(reached, freesAt));
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

        // Kept for the app's household alone, under the conversation, and named on the message: the agent
        // fetches them on this channel for the turn's person, so a follow-up's history puts them back too.
        var attachments = await KeepAsync(app, conversation, images, ct);
        var reply = await turns.SendAsync(new ChannelMessageNotification
        {
            ConversationId = conversation,
            AgentId = app.BuildingAgent,
            Sender = viewer.UserId,
            // A label only: the agent believes the assertion, renewed with the session if it was due, or the
            // check's call token.
            Principal = new Principal(AppChannels.Apps, viewer.UserId, viewer),
            Assertion = assertion,
            CheckCallToken = checkCall,
            Content = ask.Text,
            Attachments = attachments.Count > 0 ? attachments : null,
            AppTurn = new AppTurn { AppId = app.Id, Instructions = instructions, Servers = served.Manifest.Asks },
            ResponseFormat = ask.Schema is { } schema ? new ResponseFormat { Name = schema.Name, Schema = schema.Schema } : null,
            ConfigPatch = served.Manifest.Model is { } model ? new AgentConfigPatch { Model = model } : null,
            Timestamp = clock.GetUtcNow()
        }, wait, clock, ct);

        // Never delivered: neither the count, a conversation opened for it nor its images are kept, so a page
        // retrying while nobody listens spends nothing and fills no table or volume.
        if (reply is TurnReply.NotListening)
        {
            await counts.ReleaseAsync(app, counted);
            uploads.Remove(attachments);
            if (ask.Conversation is null)
            {
                conversations.Forget(conversation);
            }
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
            // AgentTurns logged the agent's own words; the page is told only what a refusal says, but for the
            // preview, which its author alone opens, so a check can say why a turn failed.
            TurnReply.Failed { Reason: var reason } =>
                OriginAnswer.Json(StatusCodes.Status422UnprocessableEntity, AppAsks.Failed(reason, session.Origin.Preview)),
            TurnReply.Unanswered => OriginAnswer.Envelope(StatusCodes.Status422UnprocessableEntity, AppAsks.Unanswered),
            TurnReply.NotListening => OriginAnswer.Envelope(StatusCodes.Status503ServiceUnavailable, AppAsks.NotListening),
            TurnReply.TimedOut => OriginAnswer.Envelope(StatusCodes.Status504GatewayTimeout, AppAsks.TimedOut(wait)),
            _ => throw new InvalidOperationException($"A turn's reply is answered, failed, unanswered, unheard or timed out: {reply}.")
        };
    }

    // An ask is its JSON, as a body of its own or, carrying images, as the one field of a form beside them
    // (AppAsks.AskField, FilesField). Each is bounded before it is read whole, and a form naming anything
    // else is refused rather than read as though it had not.
    private async Task<AskBody> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return await AppPages.BodyAsync(request, MaxBodyBytes, ct) is { } body
                ? new AskBody.Read(body, [])
                : new AskBody.Refused($"An ask's body is one JSON object of at most {MaxBodyBytes} bytes.");
        }

        var shape = $"An ask with images is a form of one {AppAsks.AskField} field, the ask's JSON, and at most "
                    + $"{settings.Asks.MaxImages} {AppAsks.FilesField} of at most {settings.Asks.MaxImageBytes} bytes each.";
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxFormBytes;
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = MaxFormBytes,
                ValueLengthLimit = MaxBodyBytes,
                ValueCountLimit = 16
            }, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return new AskBody.Refused(shape);
        }

        if (form.Count != 1 || form[AppAsks.AskField] is not [{ } json] || form.Files.Any(f => f.Name != AppAsks.FilesField))
        {
            return new AskBody.Refused(shape);
        }

        // One at a time: every part reads from the one buffered body.
        var images = new List<AppAskUpload>(form.Files.Count);
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            images.Add(new AppAskUpload(file.FileName, buffer.ToArray()));
        }

        return new AskBody.Read(System.Text.Encoding.UTF8.GetBytes(json), images);
    }

    // In the order sent, so the references keep the order of the images.
    private async Task<IReadOnlyList<AttachmentReference>> KeepAsync(
        BuiltApp app, string conversation, IReadOnlyList<AppAskImage> images, CancellationToken ct)
    {
        var kept = new List<AttachmentReference>(images.Count);
        foreach (var image in images)
        {
            kept.Add(await uploads.SaveForHouseholdAsync(
                app.HouseholdId, conversation, image.FileName, image.MediaType, image.Bytes, ct));
        }

        return kept;
    }

    private abstract record AskBody
    {
        public sealed record Read(byte[] Json, IReadOnlyList<AppAskUpload> Uploads) : AskBody;

        public sealed record Refused(string Problem) : AskBody;
    }

    private static TimeSpan Min(TimeSpan one, TimeSpan other) => one <= other ? one : other;

    private static OriginAnswer Answered(AppAskAnswer answer) =>
        new(StatusCodes.Status200OK, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(answer, JsonSerializerOptions.Web));

    private static OriginAnswer InvalidAsk(string problem) =>
        OriginAnswer.Envelope(StatusCodes.Status400BadRequest, ToolError.Result(ToolError.Codes.InvalidArgument, problem));
}