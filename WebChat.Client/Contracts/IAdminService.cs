using Domain.DTOs.Household;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

// The admin API on the accounts service, as the household's owners reach it from the admin area.
public interface IAdminService
{
    Task<AdminAnswer<HouseholdSettings>> GetSettingsAsync();

    Task<AdminAnswer<HouseholdSettings>> SaveSettingsAsync(HouseholdSettings settings);
}