namespace Domain.DTOs;

// Which model and which provider actually answered, as opposed to the ones configured. Routing
// picks an endpoint per request, so a turn that behaved badly is only diagnosable against the
// route it ran on: a routing surprise and a prompt defect look identical without it.
// The provider is null on the Responses wire, which reports only the generation it created. The
// id is what a caller who needs the provider name asks OpenRouter about afterwards — a lookup
// that costs a request, so nothing on the turn's own path does it.
public sealed record ServedRoute(string? Model, string? Provider, string? GenerationId = null);