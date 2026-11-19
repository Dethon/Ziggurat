using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;

namespace Domain.Contracts;

// The household's glucose watches as the /watchers mount keeps them. A watch is the household's (its
// id is unique there), not one person's log: who may see it is its subject, its writer and whoever
// holds everyone's Sugary log, so every read names the account asking and the store keeps to that
// account's household, and the list keeps to what that account is shown. Implemented by Sugary's store.
public interface IGlucoseWatches
{
    // The watches of the reader's household whose subject or writer the reader is, or every one of them
    // when everyone is true (a reader the household lets read everyone's log).
    Task<IReadOnlyList<StoredGlucoseWatch>> ListAsync(Account reader, bool everyone, CancellationToken ct);

    // One watch of the reader's household, whoever may see it: an id is the household's, so a write must
    // know an id is taken even where the writer is not shown the watch.
    Task<StoredGlucoseWatch?> FindAsync(Account reader, string id, CancellationToken ct);

    // A new watch in the writer's household; false when the household already has one by that id.
    Task<bool> AddAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct);

    // The watch of that id written anew, and what was judged of its triggers forgotten, since a trigger is
    // known by its place in the list; false when it is gone.
    Task<bool> ReplaceAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct);

    // False when there was no such watch.
    Task<bool> RemoveAsync(Account reader, string id, CancellationToken ct);

    // What the engine last judged of each trigger, by index; empty until it has judged any.
    Task<IReadOnlyList<GlucoseTriggerCondition>> ConditionsAsync(Account reader, string id, CancellationToken ct);
}