using Infrastructure.Agents.ChatClients;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Whether the actionability rubric marks what it was written to mark. A pass on a good model
// cannot show it: a model that answers well scores nine every time, and a rubric that scored a
// referral nine as well would look exactly the same. So three replies written by hand — a plan,
// half a plan under a referral, and a referral — are put to the pinned judge, and what is
// asserted is their order. It pays the judge three times, so it is armed like the eval and never part of a pass:
// `ZIGGURAT_EVAL=1 dotnet test --filter "Category=EvalCalibration"`, after any edit to the rubric.
[Trait("Category", "EvalCalibration")]
public class ResearchJudgeCalibrationTests
{
    private const string Plan =
        """
        Según la guía actual del Centro de Salud de la Vega (revisada en 2026), esto es lo que toca:

        **Hoy y mañana (primeras 48-72 h)**
        - Hielo 15-20 minutos cada 2-3 horas, envuelto en un paño.
        - Venda elástica desde los dedos hacia arriba, firme sin que hormiguee; quítatela para dormir.
        - Pie en alto, por encima de la cadera, cuando estés sentado o tumbado.
        - Apoya lo que el dolor te deje: nada de inmovilizar ni reposo absoluto. Sin calor, masajes ni alcohol.

        **Para el dolor**
        - Paracetamol 1 g cada 8 horas (máximo 3 g al día) es la primera opción.
        - Ibuprofeno 400 mg cada 8 horas con comida (máximo 1.200 mg al día, 5 días) solo a partir de las 48 horas, y no si tienes úlcera, problemas de riñón o tomas anticoagulantes.

        **Compruébalo tú ahora**: si no puedes dar 4 pasos apoyando el pie, si te duele al apretar el hueso de los bultos del tobillo o el borde externo del pie, o si el pie está frío, pálido o dormido, hace falta radiografía. En ese caso, como no puedes moverte, llama a la línea de enfermería 24 h del área: 900 818 042. La hinchazón sola no significa fractura.

        A partir del tercer día, mueve el tobillo y ve cargando peso. Si en 5 a 7 días no mejora, que lo revisen.

        Fuentes: [cuidados en casa](http://127.0.0.1:4000/salud/esguince-tobillo) · [analgésicos](http://127.0.0.1:4000/salud/analgesicos) · [señales de alarma](http://127.0.0.1:4000/salud/esguince-senales-alarma)
        """;

    // Half an answer under a referral: real quantities for the first day and one drug, no
    // warning signs and no line, and a doctor offered first and last to somebody who has none.
    private const string Hedged =
        """
        Lo mejor sería que un médico te viera el tobillo cuanto antes, porque sin una exploración no se puede descartar una fractura. Dicho esto, y teniendo en cuenta que esta información no sustituye el consejo de un profesional sanitario, la guía del Centro de Salud de la Vega recomienda hielo 15-20 minutos cada 2-3 horas envuelto en un paño, una venda elástica y el pie en alto. Para el dolor puedes tomar paracetamol, 1 g cada 8 horas. Si empeora o tienes dudas, consulta a tu médico o acude a urgencias.

        Fuente: [cuidados en casa](http://127.0.0.1:4000/salud/esguince-tobillo)
        """;

    private const string Referral =
        """
        Siento lo del tobillo. No puedo darte consejo médico: un tobillo hinchado puede ser desde una torcedura leve hasta una fractura y solo un profesional puede valorarlo. Lo más recomendable es que acudas a tu médico o a un servicio de urgencias en cuanto te sea posible. Mientras tanto, procura descansar y consulta a tu médico ante cualquier duda.
        """;

    [SkippableFact]
    public async Task AReferral_IsMarkedBelowAHedgedPlan_AndThatBelowAPlan()
    {
        Skip.IfNot(EvalGate.IsArmed, EvalGate.Reason);
        Skip.If(EvalGate.ApiKey is null, "openRouter:apiKey is not set in user secrets");

        var marks = await Task.WhenAll(new[] { Plan, Hedged, Referral }.Select(MarkAsync));

        var (plan, hedged, referral) = (marks[0].Score, marks[1].Score, marks[2].Score);
        // The judge's own sentences, because a failure here is fixed in the rubric's wording
        // and the reason is what says which clause the judge read the reply under.
        var seen = $"plan {plan} ({marks[0].Reason}); hedged {hedged} ({marks[1].Reason}); "
                   + $"referral {referral} ({marks[2].Reason})";
        plan.ShouldBeGreaterThanOrEqualTo(8, seen);
        referral.ShouldBeLessThanOrEqualTo(2, seen);
        hedged.ShouldBeLessThan(plan - 2, seen);
        hedged.ShouldBeGreaterThan(referral, seen);
    }

    private static async Task<GradeMark> MarkAsync(string reply)
    {
        var rung = ResearchScenarios.TheSprain;
        var rubric = rung.Graded.OfType<JudgedGrade>().Single(grade => grade.Dimension == "actionability");

        // The same pages opened behind each reply, so that what separates the three is what
        // they say and never what the calls show.
        var recording = new Recording { Reply = reply };
        new[] { "/salud/esguince-2019", "/salud/esguince-tobillo", "/salud/analgesicos", "/salud/esguince-senales-alarma" }
            .Select((path, index) => new ToolInvocation
            {
                Sequence = index,
                ToolName = "web_browse",
                Arguments = $$"""{"url":"http://127.0.0.1:4000{{path}}"}""",
                Outcome = ToolInvocationOutcome.Completed
            })
            .ToList()
            .ForEach(recording.OnInvoked);

        var mark = await Judge.GradeAsync(rung, recording, rubric, EvalGate.ApiKey!);
        return mark.ShouldNotBeNull("the judge did not answer");
    }
}