using System.Text;
using System.Text.RegularExpressions;
using McpChannelTelegram.Services;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

public class ApprovalCallbackRouterTests
{
    private readonly ApprovalCallbackRouter _sut = new();
    private readonly Mock<ITelegramBotClient> _botClient = new();

    // The person whose message started the turn the approval belongs to.
    private static readonly IReadOnlySet<long> Asker = new HashSet<long> { 1 };

    [Theory]
    [InlineData("tool_approve", "approved")]
    [InlineData("tool_always", "approved_and_remember")]
    [InlineData("tool_reject", "denied")]
    public async Task RegisterApproval_ThenCallback_ReturnsExpectedResult(string callbackPrefix, string expectedResult)
    {
        var (approvalId, resultTask) = _sut.RegisterApproval(TimeSpan.FromSeconds(10), Asker, CancellationToken.None);

        var query = CreateCallbackQuery($"{callbackPrefix}:{approvalId}");
        await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        var result = await resultTask;
        result.ShouldBe(expectedResult);
    }

    [Fact]
    public async Task RegisterApproval_Timeout_ReturnsDenied()
    {
        var (_, resultTask) = _sut.RegisterApproval(TimeSpan.FromMilliseconds(50), Asker, CancellationToken.None);

        var result = await resultTask;
        result.ShouldBe("denied");
    }

    [Fact]
    public async Task RegisterApproval_Cancelled_ReturnsDenied()
    {
        using var cts = new CancellationTokenSource();
        var (_, resultTask) = _sut.RegisterApproval(TimeSpan.FromSeconds(30), Asker, cts.Token);

        cts.Cancel();

        var result = await resultTask;
        result.ShouldBe("denied");
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_EmptyData_ReturnsFalse()
    {
        var query = CreateCallbackQuery(null);

        var handled = await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_UnknownPrefix_ReturnsFalse()
    {
        var query = CreateCallbackQuery("some_random_data");

        var handled = await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_ExpiredApproval_AnswersExpired()
    {
        var query = CreateCallbackQuery("tool_approve:nonexistent");

        var handled = await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeTrue();
        _botClient.Verify(b => b.SendRequest(
            It.Is<Telegram.Bot.Requests.AnswerCallbackQueryRequest>(r =>
                r.Text == "This approval request has expired."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void CreateApprovalKeyboard_ContainsThreeButtons()
    {
        var keyboard = ApprovalCallbackRouter.CreateApprovalKeyboard("abc123");

        var buttons = keyboard.InlineKeyboard.First().ToList();
        buttons.Count.ShouldBe(3);
        buttons[0].CallbackData.ShouldBe("tool_approve:abc123");
        buttons[1].CallbackData.ShouldBe("tool_always:abc123");
        buttons[2].CallbackData.ShouldBe("tool_reject:abc123");
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_Approve_EditsMessage()
    {
        var (approvalId, _) = _sut.RegisterApproval(TimeSpan.FromSeconds(10), Asker, CancellationToken.None);

        var query = CreateCallbackQuery($"tool_approve:{approvalId}", withMessage: true);
        await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<Telegram.Bot.Requests.EditMessageTextRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // An approval id is a whole GUID, so one cannot be guessed, and every button still fits the
    // 64 bytes Telegram allows a callback's data.
    [Fact]
    public void CreateApprovalKeyboard_EveryButton_CarriesAFullGuidWithinTheCallbackLimit()
    {
        var (approvalId, _) = _sut.RegisterApproval(TimeSpan.FromSeconds(10), Asker, CancellationToken.None);

        var buttons = ApprovalCallbackRouter.CreateApprovalKeyboard(approvalId).InlineKeyboard.First().ToList();

        buttons.ShouldAllBe(b => Regex.IsMatch(b.CallbackData!, "^tool_[a-z]+:[0-9a-f]{32}$"));
        buttons.ShouldAllBe(b => Encoding.UTF8.GetByteCount(b.CallbackData!) <= 64);
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_SomebodyElsesTap_AnswersNotYoursAndStaysPending()
    {
        var (approvalId, resultTask) = _sut.RegisterApproval(TimeSpan.FromSeconds(10), Asker, CancellationToken.None);

        var query = CreateCallbackQuery($"tool_approve:{approvalId}", withMessage: true, fromUserId: 2);
        var handled = await _sut.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeTrue();
        resultTask.IsCompleted.ShouldBeFalse();
        _botClient.Verify(b => b.SendRequest(
            It.Is<Telegram.Bot.Requests.AnswerCallbackQueryRequest>(r => r.Text == "This approval isn't yours"),
            It.IsAny<CancellationToken>()), Times.Once);
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<Telegram.Bot.Requests.EditMessageTextRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // The asker can still answer it afterwards.
        await _sut.HandleCallbackQueryAsync(
            _botClient.Object, CreateCallbackQuery($"tool_reject:{approvalId}"), CancellationToken.None);
        (await resultTask).ShouldBe("denied");
    }

    private static CallbackQuery CreateCallbackQuery(string? data, bool withMessage = false, long fromUserId = 1)
    {
        var query = new CallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new User { Id = fromUserId, IsBot = false, FirstName = "Test" }
        };

        if (withMessage)
        {
            query.Message = new Message
            {
                Id = 42,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = 100, Type = Telegram.Bot.Types.Enums.ChatType.Private }
            };
        }

        return query;
    }
}