using Domain.Agents;
using Domain.DTOs;

namespace Infrastructure.Agents;

// Where a delegated task goes instead of to a real worker. Absent in every deployment — like
// IToolInvocationObserver, only an evaluation harness registers one, and for the same reason: what
// a scenario about delegation is asking is whether the parent decided to delegate, which profile
// it named, and what it wrote in the prompt. A real worker answering makes that question expensive
// and puts a second model's answer between the assertion and the behaviour it is about.
public interface ISubAgentSpawner
{
    DisposableAgent Spawn(SubAgentDefinition definition);
}