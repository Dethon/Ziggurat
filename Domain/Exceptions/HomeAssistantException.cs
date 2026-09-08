namespace Domain.Exceptions;

public class HomeAssistantException : Exception
{
    public int? StatusCode { get; }

    public HomeAssistantException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

public sealed class HomeAssistantUnauthorizedException(string message)
    : HomeAssistantException(message, 401);

public sealed class HomeAssistantNotFoundException(string message)
    : HomeAssistantException(message, 404);

// Home Assistant validated a configuration and refused it. The message is the home's own text —
// which key was missing, which trigger is unknown — because the caller fixes the config from it.
public sealed class HomeAssistantConfigRejectedException(string message)
    : HomeAssistantException(message, 400);

public sealed class MusicAssistantException(string message, Exception? inner = null)
    : Exception(message, inner);