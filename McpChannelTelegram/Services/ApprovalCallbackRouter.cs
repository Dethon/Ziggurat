using System.Collections.Concurrent;
using System.Text;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace McpChannelTelegram.Services;

// Whether the Telegram id behind a tap on an approval's keyboard may answer it, asked as each tap
// comes: the asker's account, or a fire's household, is something only the accounts service can say.
public delegate Task<bool> ApprovalAnswerer(long telegramId, CancellationToken ct);

public sealed class ApprovalCallbackRouter
{
    private const string ApproveCallbackPrefix = "tool_approve:";
    private const string AlwaysCallbackPrefix = "tool_always:";
    private const string RejectCallbackPrefix = "tool_reject:";

    private readonly ConcurrentDictionary<string, ApprovalWaiter> _pending = new();

    public (string ApprovalId, Task<string> ResultTask) RegisterApproval(
        TimeSpan timeout, IReadOnlySet<long> answerers, CancellationToken cancellationToken) =>
        RegisterApproval(timeout, (tapper, _) => Task.FromResult(answerers.Contains(tapper)), cancellationToken);

    // The question keeps the id the agent gave it, so it can be settled by it; one asked without an id
    // mints its own.
    public (string ApprovalId, Task<string> ResultTask) RegisterApproval(
        TimeSpan timeout, ApprovalAnswerer mayAnswer, CancellationToken cancellationToken, string? approvalId = null)
    {
        var waiter = new ApprovalWaiter(timeout, mayAnswer, cancellationToken);
        // The whole GUID, not a prefix of it: 32 hex characters behind the longest prefix
        // ("tool_always:") is 44 bytes, inside the 64 Telegram allows a callback's data.
        var id = approvalId is not null && Guid.TryParseExact(approvalId, "N", out _) && _pending.TryAdd(approvalId, waiter)
            ? approvalId
            : Mint(waiter);

        var resultTask = WaitAndCleanupAsync(id, waiter);
        return (id, resultTask);
    }

    private string Mint(ApprovalWaiter waiter)
    {
        var approvalId = Guid.NewGuid().ToString("N");
        _pending[approvalId] = waiter;
        return approvalId;
    }

    // Where the question is showing, so an answer — here or elsewhere — reaches every copy of it.
    public void Shown(string approvalId, ITelegramBotClient botClient, long chatId, int messageId)
    {
        if (_pending.TryGetValue(approvalId, out var waiter))
        {
            waiter.Shown(new ShownMessage(botClient, chatId, messageId));
        }
    }

    // The question put in its asker's private chat with the bot, whose id is their own Telegram id,
    // on the same keyboard, and kept with its other copies: where a group's question reaches its asker
    // alone, and where another channel's is offered to them. False for a person who linked no Telegram;
    // a chat the person never opened with this bot, or an accounts service that could not say which it
    // is, throws, for each caller to weigh. Sent whatever the call's token says, so a copy that went
    // out is always recorded for the answer to edit.
    public async Task<bool> ShowInPrivateChatAsync(
        string approvalId, Account asker, IReadOnlyList<ToolApprovalRequest> requests, ITelegramBotClient botClient,
        TelegramAccess access, CancellationToken ct)
    {
        if (await access.PrivateChatOfAsync(asker, ct) is not { } privateChat)
        {
            return false;
        }

        var sent = await botClient.SendMessage(
            privateChat, FormatApprovalMessage(requests), ParseMode.Html,
            replyMarkup: CreateApprovalKeyboard(approvalId), cancellationToken: CancellationToken.None);
        Shown(approvalId, botClient, privateChat, sent.MessageId);
        return true;
    }

    // Answered on another of the asker's channels: every copy says so and asks nothing more, and the
    // question here is no answer. What the answer was is the other channel's to show.
    public async Task SettleAsync(string approvalId, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(approvalId, out var waiter))
        {
            return;
        }

        waiter.SetResult(ChannelProtocol.NoAnswer);
        await EditAllAsync(waiter, "Answered elsewhere", cancellationToken);
    }

    public async Task<bool> HandleCallbackQueryAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var data = callbackQuery.Data;
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        string? approvalId;
        string result;

        if (data.StartsWith(ApproveCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[ApproveCallbackPrefix.Length..];
            result = "approved";
        }
        else if (data.StartsWith(AlwaysCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[AlwaysCallbackPrefix.Length..];
            result = "approved_and_remember";
        }
        else if (data.StartsWith(RejectCallbackPrefix, StringComparison.Ordinal))
        {
            approvalId = data[RejectCallbackPrefix.Length..];
            result = "denied";
        }
        else
        {
            return false;
        }

        if (!_pending.TryGetValue(approvalId, out var waiter))
        {
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "This approval request has expired.",
                cancellationToken: cancellationToken);
            return true;
        }

        // Everyone in a group chat sees the keyboard; only the people the approval was put to may
        // answer it. Anyone else is told so, and the approval keeps waiting for one of them. A tap
        // the accounts service could not be asked about is neither, and is asked to come again.
        bool mayAnswer;
        try
        {
            mayAnswer = await waiter.MayAnswer(callbackQuery.From.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "I can't check who you are right now. Try again in a moment.",
                cancellationToken: cancellationToken);
            return true;
        }

        if (!mayAnswer)
        {
            await botClient.AnswerCallbackQuery(
                callbackQuery.Id,
                "This approval isn't yours",
                cancellationToken: cancellationToken);
            return true;
        }

        waiter.SetResult(result);

        var responseText = result == "denied" ? "Rejected" : "Approved";
        var icon = result == "denied" ? "\u274c" : "\u2705";
        await botClient.AnswerCallbackQuery(
            callbackQuery.Id,
            $"{icon} {responseText}",
            cancellationToken: cancellationToken);

        if (callbackQuery.Message is not null)
        {
            waiter.Shown(new ShownMessage(botClient, callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId));
        }

        // Every copy of the question — the group's and the asker's private chat's — says what it was.
        await EditAllAsync(waiter, $"{icon} {responseText}", cancellationToken);
        return true;
    }

    private static Task EditAllAsync(ApprovalWaiter waiter, string text, CancellationToken cancellationToken) =>
        Task.WhenAll(waiter.ShownMessages.Select(async shown =>
        {
            try
            {
                await shown.BotClient.EditMessageText(
                    shown.ChatId, shown.MessageId, text, cancellationToken: cancellationToken);
            }
            catch
            {
                // Best effort — message may have been deleted
            }
        }));

    // The question as both of its senders put it: the group or chat that asked, and a private chat it is
    // offered or copied to.
    public static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        var toolNames = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));
        sb.AppendLine($"<b>🔧 Approval Required:</b> <code>{HtmlEncode(toolNames)}</code>");

        foreach (var request in requests)
        {
            if (request.Arguments.Count == 0)
            {
                continue;
            }

            var details = new StringBuilder();
            foreach (var (key, value) in request.Arguments)
            {
                var formatted = value?.ToString()?.Replace("\n", " ") ?? "null";
                if (formatted.Length > 100)
                {
                    formatted = formatted[..100] + "...";
                }

                details.AppendLine($"<i>{HtmlEncode(key)}:</i> {HtmlEncode(formatted)}");
            }

            sb.AppendLine($"<blockquote expandable>{details.ToString().TrimEnd()}</blockquote>");
        }

        // Why the exec screen put this to the person, last so it sits right above the keyboard.
        var reason = ExecScreenReasons.English([.. requests.SelectMany(r => r.Screen ?? []).Distinct()]);
        if (reason is not null)
        {
            sb.AppendLine($"<i>Why you're asked: {HtmlEncode(reason)}</i>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    public static InlineKeyboardMarkup CreateApprovalKeyboard(string approvalId)
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("\u2705 Approve", $"{ApproveCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("\ud83d\udd01 Always", $"{AlwaysCallbackPrefix}{approvalId}"),
                InlineKeyboardButton.WithCallbackData("\u274c Reject", $"{RejectCallbackPrefix}{approvalId}")
            ]
        ]);
    }

    private async Task<string> WaitAndCleanupAsync(string approvalId, ApprovalWaiter waiter)
    {
        try
        {
            return await waiter.Task;
        }
        catch (OperationCanceledException)
        {
            return "denied";
        }
        finally
        {
            _pending.TryRemove(approvalId, out _);
            waiter.Dispose();
        }
    }

    private sealed record ShownMessage(ITelegramBotClient BotClient, long ChatId, int MessageId);

    private sealed class ApprovalWaiter : IDisposable
    {
        private readonly TaskCompletionSource<string> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenSource _linkedCts;
        private readonly CancellationTokenRegistration _registration;

        public ApprovalWaiter(
            TimeSpan timeout, ApprovalAnswerer mayAnswer, CancellationToken cancellationToken)
        {
            MayAnswer = mayAnswer;
            _timeoutCts = new CancellationTokenSource(timeout);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeoutCts.Token);
            _registration = _linkedCts.Token.Register(() => _tcs.TrySetCanceled(_linkedCts.Token));
        }

        public ApprovalAnswerer MayAnswer { get; }

        public Task<string> Task => _tcs.Task;

        private readonly ConcurrentDictionary<(long, int), ShownMessage> _shown = new();

        public IReadOnlyList<ShownMessage> ShownMessages => [.. _shown.Values];

        public void Shown(ShownMessage message) => _shown.TryAdd((message.ChatId, message.MessageId), message);

        public void SetResult(string result) => _tcs.TrySetResult(result);

        public void Dispose()
        {
            _registration.Dispose();
            _linkedCts.Dispose();
            _timeoutCts.Dispose();
        }
    }
}