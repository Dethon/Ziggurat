using Microsoft.AspNetCore.SignalR;

namespace WebChat.Client.Helpers;

// What the hub said when it refused a call: SignalR hands a HubException's message on prefixed with
// where it was thrown, and a person is shown the hub's own words.
public static class HubRefusal
{
    private const string Marker = "HubException: ";

    public static string MessageOf(HubException refusal) =>
        refusal.Message.LastIndexOf(Marker, StringComparison.Ordinal) is var at and >= 0
            ? refusal.Message[(at + Marker.Length)..]
            : refusal.Message;
}