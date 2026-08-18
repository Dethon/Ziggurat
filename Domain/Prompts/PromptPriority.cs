namespace Domain.Prompts;

// The order sections reach the model in, and the whole of what "wins" means here: a section
// further down sits closer to the conversation, so when two of them speak to the same rule the
// later one is the one the model applies. Every ordering decision that used to be a Prepend or an
// Append in the assembly code is one of these bands, and the comment that justified it travels
// with the band rather than with the call.
//
// Values are spaced so a new band lands between two existing ones without renumbering, which
// matters because the numbers appear in snapshots.
public enum PromptPriority
{
    // What the agent is for, before it is told what it can do.
    CoreDirective = 100,

    // Which agent it is. Ahead of every feature prompt, so a tool description is read by someone.
    Identity = 200,

    // Who it is talking to. Scoping for every user-scoped tool call that follows.
    UserContext = 300,

    // Domain tool features the agent opted into: memory, subagents.
    Feature = 400,

    // The mounts this session actually has, which is a per-session fact rather than a configured
    // one — it is built after the servers answer.
    FileSystem = 500,

    // Prompts served by the MCP servers themselves. Their text belongs to the server; only their
    // budget, purpose and place are declared here.
    Client = 600,

    // The one section that changes on its own. It goes after every static section because the
    // provider's prompt cache keys on a byte prefix, and dating the opening line threw the whole
    // cached prefix away at every midnight.
    Date = 700,

    // Per-agent configuration, closest to the conversation and so the least "lost in the middle".
    CustomInstructions = 800,

    // How this agent's replies are consumed — spoken aloud rather than read. It outranks the
    // formatting and verbosity guidance of every section above it, and says so.
    ChannelOverride = 900,

    // A hard output constraint, and the one thing every section above it contradicts by being
    // written in English.
    Language = 1000
}