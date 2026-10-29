using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

// The accounts service is on this page's own host, under /accounts, which the proxy routes there
// unchecked: the session cookie rides along, and it answers who is signed in or 401.
public sealed class AccountService(HttpClient httpClient, NavigationManager navigation) : IAccountService
{
    public async Task<SignedInPerson?> GetSignedInAsync()
    {
        using var response = await httpClient.GetAsync("/accounts/me");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedInPerson>();
    }

    // A full load, so the proxy and the accounts service see the request; sign-in comes back here.
    public void SendToSignIn() =>
        navigation.NavigateTo($"/accounts/sign-in?returnUrl={Uri.EscapeDataString(navigation.Uri)}", forceLoad: true);
}