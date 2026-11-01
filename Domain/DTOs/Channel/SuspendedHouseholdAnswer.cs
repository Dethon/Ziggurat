using System.Net;

namespace Domain.DTOs.Channel;

// How the accounts service's internal API answers a channel server asking after somebody of a household
// the operator suspended — a resolution, the guest, a Telegram start or web link: neither the person,
// whom the channel would then vouch for, nor nobody (404), whom Telegram would tell to ask for an
// invite, but a status of its own, so the channel tells them their household is suspended and starts no
// turn. Spelled once for the service and its client.
public static class SuspendedHouseholdAnswer
{
    public const HttpStatusCode Status = HttpStatusCode.Locked;
}