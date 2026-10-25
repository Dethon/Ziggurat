namespace Domain.Contracts;

// A person's remembered logins exist and this server cannot open them — no key, or the wrong one.
// Thrown rather than answered as nothing: a person silently logged out of everything is the
// failure the store exists to refuse, and every browse or handoff as them refuses instead.
public class PersonStateUnreadableException(string person, string reason)
    : Exception($"The remembered logins of '{person}' cannot be read: {reason}.")
{
    public string Person { get; } = person;
}