using Domain.DTOs.Admin;

namespace WebChat.Client.Models;

// What the admin API answered, as the admin area tells them apart: the value (and a warning it stood
// with), a change refused with its problems, a page signed out since it loaded, a person the API
// will not let do this, or an API that could not be asked (with why, when it answered that it cannot
// serve this just now).
public abstract record AdminAnswer<T>
{
    public sealed record Answered(T Value, string? Warning = null) : AdminAnswer<T>;

    public sealed record Refused(AdminRefusal Refusal) : AdminAnswer<T>;

    public sealed record SignedOut : AdminAnswer<T>;

    public sealed record NotAllowed(string Message) : AdminAnswer<T>;

    public sealed record Unreachable(string? Message = null) : AdminAnswer<T>;
}