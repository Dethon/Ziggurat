using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// A turn a service started — a finished login, a schedule, a watch, the Sugary app — carries the
// model's instructions as its message. The person is shown a line saying what happened, not those
// instructions as if somebody had written them.
public class ServiceNoticeTests
{
    [Theory]
    [InlineData(ServiceSenders.Login, "You finished logging in")]
    [InlineData(ServiceSenders.Scheduler, "A scheduled task ran")]
    [InlineData(ServiceSenders.Watch, "A home watch fired")]
    [InlineData(ServiceSenders.SugaryApp, "Sent from the Sugary app")]
    [InlineData(ChannelProtocol.SystemSender, "A notice from the system")]
    public void AServicesMessage_IsALineSayingWhatHappened(string service, string line)
    {
        var message = new ChatMessageModel { Role = "user", Content = "[Login handoff finished] Carry on…", SenderName = service };

        ServiceNotice.Of(message).ShouldBe(line);
    }

    [Fact]
    public void AServiceStreamedLive_NamedOnlyAsItsSender_IsALineToo() =>
        ServiceNotice.Of(new ChatMessageModel { Role = "user", Content = "…", SenderId = ServiceSenders.Scheduler })
            .ShouldBe("A scheduled task ran");

    // The Sugary app's turns carry the person's account, so its request is known by what it asks
    // for — a structured answer no typed message asks for — not by its sender.
    [Theory]
    [InlineData("meal_estimate")]
    [InlineData("nutrition_label")]
    public void ARequestAnAppComposed_IsALineNamingTheApp(string format)
    {
        var message = new ChatMessageModel
        {
            Role = "user", Content = "There is no photo of this meal…", SenderId = "5bb91173dce8c239fee503c85cdb0645",
            SenderName = "Marta Ortega", ResponseFormat = format
        };

        ServiceNotice.Of(message).ShouldBe("Sent from the Sugary app");
    }

    [Fact]
    public void ARequestForAFormatNobodyNamed_IsALineToo() =>
        ServiceNotice.Of(new ChatMessageModel { Role = "user", Content = "…", SenderId = "x", ResponseFormat = "something_else" })
            .ShouldBe("Sent from an app");

    [Theory]
    [InlineData("user", "5bb91173dce8c239fee503c85cdb0645", "Leo Ortega")]
    [InlineData("user", "5bb91173dce8c239fee503c85cdb0645", "login")]
    [InlineData("assistant", null, "login")]
    public void APersonsMessage_OrAnAnswer_IsNoNotice(string role, string? senderId, string senderName) =>
        ServiceNotice.Of(new ChatMessageModel { Role = role, Content = "hola", SenderId = senderId, SenderName = senderName })
            .ShouldBeNull();

    // A fire's turn reaches its topic framed with the server's report for the model; behind the toggle a
    // person read Tomás's reminder as '<fire-report note="what the server that raised this turn reports...'
    // before what the reminder asked.
    [Fact]
    public void WhatANoticeShows_IsWhatWasAsked_WithoutTheReportFramedForTheModel()
    {
        var framed = FireReports.Frame("Avisa a Tomás: que riegue las plantas.", "This turn is the scheduled task \"plantas\".");

        ServiceNotice.PromptOf(new ChatMessageModel { Role = "user", Content = framed })
            .ShouldBe("Avisa a Tomás: que riegue las plantas.");
        ServiceNotice.PromptOf(new ChatMessageModel { Role = "user", Content = "[login finished]" })
            .ShouldBe("[login finished]");
    }
}