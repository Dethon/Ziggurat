namespace Domain.Exceptions;

// The accounts service answered, and vouches for nobody of the household asked about: the operator
// suspended it (SuspendedHouseholdAnswer). Neither an outage nor a stranger, so a channel tells the
// person why and starts no turn, rather than asking them to try again or for an invite.
public sealed class HouseholdSuspendedException()
    : Exception("The household is suspended, so the accounts service vouches for nobody of it.");