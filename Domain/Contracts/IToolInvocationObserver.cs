using Domain.DTOs;

namespace Domain.Contracts;

// The one seam an evaluation harness needs and no deployment registers. It is resolved from the
// container, absent by default, and every implementation of it lives in the tests: what a turn
// did is otherwise only reconstructible by re-parsing streamed function-call content that the
// function-invoking client has already parsed.
public interface IToolInvocationObserver
{
    void OnInvoked(ToolInvocation invocation);
}