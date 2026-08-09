namespace WebChat.Client.Components;

public static class ChatInputLogic
{
    // A message with attachments and no text at all is a normal thing to send: nobody should be
    // made to type something meaningless before a photo. A file still uploading holds the send,
    // because it has no reference to travel with yet.
    public static bool CanSend(
        bool disabled,
        string? inputText,
        bool isStreaming,
        int readyAttachments = 0,
        bool uploadInFlight = false,
        string? capabilityRefusal = null) =>
        !disabled
        && !isStreaming
        && !uploadInFlight
        && capabilityRefusal is null
        && (!string.IsNullOrWhiteSpace(inputText) || readyAttachments > 0);
}