using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Channels;

public enum ChannelInboxItemKind
{
    Message,
    Cancel,
    // An insistent alert for one person, which the agent rings on their channels; and its stop.
    Alert,
    AlertStopped
}

[PublicAPI]
public sealed record ChannelInboxItem
{
    public required ChannelInboxItemKind Kind { get; init; }
    public ChannelMessageNotification? Message { get; init; }
    public ChannelCancelNotification? Cancel { get; init; }
    public ChannelAlertNotification? Alert { get; init; }
    public ChannelAlertStoppedNotification? AlertStopped { get; init; }

    public static ChannelInboxItem ForMessage(ChannelMessageNotification message) =>
        new() { Kind = ChannelInboxItemKind.Message, Message = message };

    public static ChannelInboxItem ForCancel(ChannelCancelNotification cancel) =>
        new() { Kind = ChannelInboxItemKind.Cancel, Cancel = cancel };

    public static ChannelInboxItem ForAlert(ChannelAlertNotification alert) =>
        new() { Kind = ChannelInboxItemKind.Alert, Alert = alert };

    public static ChannelInboxItem ForAlertStopped(ChannelAlertStoppedNotification stopped) =>
        new() { Kind = ChannelInboxItemKind.AlertStopped, AlertStopped = stopped };
}