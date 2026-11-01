using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.State.Admin;

// How every admin page's effect settles what the admin API answered: the value as the page's own
// outcome, a page signed out since it loaded sent to sign in and back, as the chat is, and anything
// else as the notice the page shows. True when the API answered with a value.
public static class AdminAnswers
{
    public static bool Settle<T>(
        this IDispatcher dispatcher,
        IAccountService accounts,
        AdminAnswer<T> answer,
        Func<AdminAnswer<T>.Answered, IAction> outcome,
        Func<AdminNotice, IAction> failed,
        string unreachableMessage = AdminNotice.UnreachableMessage)
    {
        switch (answer)
        {
            case AdminAnswer<T>.Answered answered:
                dispatcher.Dispatch(outcome(answered));
                return true;
            case AdminAnswer<T>.SignedOut:
                accounts.SendToSignIn();
                return false;
            case AdminAnswer<T>.Refused refused:
                dispatcher.Dispatch(failed(new AdminNotice(AdminNoticeKind.Refused, refused.Refusal.Message, refused.Refusal.Problems)));
                return false;
            case AdminAnswer<T>.NotAllowed notAllowed:
                dispatcher.Dispatch(failed(new AdminNotice(AdminNoticeKind.Unavailable, notAllowed.Message)));
                return false;
            case AdminAnswer<T>.Unreachable { Message: { } why }:
                dispatcher.Dispatch(failed(new AdminNotice(AdminNoticeKind.Unavailable, why)));
                return false;
            default:
                dispatcher.Dispatch(failed(new AdminNotice(AdminNoticeKind.Unavailable, unreachableMessage)));
                return false;
        }
    }
}