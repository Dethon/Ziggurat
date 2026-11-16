using Domain.DTOs.Household;

namespace Domain.Contracts;

// The saves of households' configuration this host hears, told once what it kept of each is dropped,
// so a host showing a household's configuration live (the chat hub's agent picker) reads the save.
public interface IHouseholdConfigSaves
{
    event Action<HouseholdConfigSaved>? Saved;

    // Starts hearing saves now rather than on the first read.
    Task ListenAsync(CancellationToken ct);
}