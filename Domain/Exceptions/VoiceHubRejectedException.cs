namespace Domain.Exceptions;

// The voice hub answered, and its answer was an error status — distinct from the hub being
// unreachable, because retrying will not fix a refused token. The HTTP adapters throw this so a
// mount that asks the hub at write time can say what the hub said instead of leaking a raw HTTP
// exception envelope; the status is kept because a 401 is configuration and a 500 is not.
public sealed class VoiceHubRejectedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}