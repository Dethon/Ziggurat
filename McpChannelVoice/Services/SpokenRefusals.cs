using Domain.Monitor;

namespace McpChannelVoice.Services;

// The agent's refusals as a satellite says them: each of RefusalAnswers in a Spanish sentence of
// voice's own, since the agent's English prose spoken by the Spanish voice is noise. Anything else an
// error reply carries is still spoken as the error it is.
public static class SpokenRefusals
{
    private static readonly Dictionary<string, string> _spoken = new(StringComparer.Ordinal)
    {
        [RefusalAnswers.Suspended] =
            "Tu hogar está suspendido, así que su asistente no contesta hasta que quien lleva el servicio lo reactive.",
        [RefusalAnswers.NotVerified] = "No pude comprobar que esto lo dijeras tú, así que no lo he contestado.",
        [RefusalAnswers.Disabled] =
            "Tu cuenta en este hogar está desactivada, así que no lo he contestado. Pregunta a quien administra el hogar.",
        [RefusalAnswers.Unchecked] =
            "Ahora mismo no pude comprobar si tu hogar me deja contestar. Inténtalo otra vez en un momento.",
        [RefusalAnswers.WithoutKey] =
            "Tu hogar no tiene clave de OpenRouter, así que su asistente todavía no puede contestar. Pide a quien lo administra que añada una.",
        [RefusalAnswers.NoVoiceAgent] =
            "Tu hogar todavía no tiene un agente de voz, así que aquí no contesta nadie. "
            + "Quien administra el hogar puede elegir uno en la página de agentes, o añadir uno con la plantilla Nabu."
    };

    public static bool TryRender(string refusal, out string spoken) =>
        _spoken.TryGetValue(refusal.Trim(), out spoken!);
}