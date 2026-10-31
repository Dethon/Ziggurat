using WebChat.Client.Contracts;
using WebChat.Client.State.Toast;

namespace WebChat.Client.State.Alert;

// The alert's Dismiss, out of the view so it can be tested, and awaited by it so its button stays
// disabled until the answer is in. A hub that answered — true, or false for an alert that already
// ended — takes it off this screen; the hub tells every other client the alert ended.
public sealed class AlertDismisser(IAlertService alerts, IDispatcher dispatcher)
{
    public async Task DismissAsync(string alertId)
    {
        var dismissed = await alerts.DismissAlertAsync(alertId);
        if (!dismissed.IsLive)
        {
            dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        dispatcher.Dispatch(new AlertEnded(alertId));
    }
}