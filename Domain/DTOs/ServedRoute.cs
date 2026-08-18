namespace Domain.DTOs;

// Which model and which provider actually answered, as opposed to the ones configured. Routing
// picks an endpoint per request, so a turn that behaved badly is only diagnosable against the
// route it ran on: a routing surprise and a prompt defect look identical without it.
public sealed record ServedRoute(string? Model, string? Provider);