using Domain.Exceptions;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Extensions;

public class WithErrorHandlingTests
{
    // A turn refused for a reason the person is told in a sentence ends on that sentence alone, so a
    // channel that says refusals in its own words (voice) recognises it.
    [Fact]
    public async Task ARefusedTurn_EndsOnItsAnswerAlone()
    {
        var updates = await Failing(new TurnRefusedException(RefusalAnswers.KeyRefused)).WithErrorHandling().ToListAsync();

        updates.ShouldHaveSingleItem().Contents.OfType<ErrorContent>().ShouldHaveSingleItem()
            .Message.ShouldBe(RefusalAnswers.KeyRefused);
    }

    [Fact]
    public async Task AnyOtherFailure_IsNamedAsAnError()
    {
        var updates = await Failing(new InvalidOperationException("boom")).WithErrorHandling().ToListAsync();

        updates.ShouldHaveSingleItem().Contents.OfType<ErrorContent>().ShouldHaveSingleItem()
            .Message.ShouldBe("An error occurred: boom");
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<AgentResponseUpdate> Failing(Exception ex)
    {
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998
}