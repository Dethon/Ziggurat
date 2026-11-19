namespace Domain.DTOs;

// A filesystem's claim to share its mount point with others. Each top-level entry under the mount
// belongs to one member; a new entry goes to the member that claims the values its written content
// holds at RouteBy (a JSON pointer, `*` standing for every item of an array), and to the member
// marked Fallback where it holds no value anybody claims. Generic on purpose: the agent routes by
// what a filesystem says, never by knowing what its entries are.
public sealed record FileSystemUnion(string RouteBy, IReadOnlyList<string> Claims, bool Fallback = false);