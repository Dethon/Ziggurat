using Domain.DTOs;

namespace Domain.Contracts;

// The one seam an evaluation harness needs and no deployment registers. It is resolved from the
// container, absent by default, and every implementation of it lives in the tests: what a turn
// did is otherwise only reconstructible by re-parsing streamed function-call content that the
// function-invoking client has already parsed.
public interface IToolInvocationObserver
{
    void OnInvoked(ToolInvocation invocation);

    // The two things that are gone the moment a turn ends: the prompt it was sent with and the
    // endpoint that answered it. A stochastic failure cannot be reproduced by re-running, so
    // whatever explains it has to be captured while it is still true.
    void OnTurn(TurnObservation turn);
}