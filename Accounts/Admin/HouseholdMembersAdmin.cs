using Accounts.Announcements;
using Accounts.Links;
using Accounts.Members;
using Accounts.People;
using Accounts.Settings;
using Accounts.Store;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.AspNetCore.Mvc;

namespace Accounts.Admin;

// The household's people as its owners manage them, over the stores the members page already uses:
// an invite (a passkey link, also redeemed in Telegram), a member's role and sees-all, disabling and
// re-enabling a member, and a recovery link for one. A link is shown in the answer that minted it and
// nowhere else — only its hash is kept, and the change log names whom it was for, never any of it.
//
// Disabling ends the member's sessions and makes them nobody to every store that vouches for a
// person (sessions, links, Telegram, voice), and is announced as a change of the household's people,
// so the agent drops the view that says who is disabled and refuses their turns and fires from the
// next one on. Their data stays: re-enabling is one column.
public static class HouseholdMembersAdmin
{
    // Disabling or re-enabling stood, but the agent was not told: it reads the household's view again
    // once what it kept runs out.
    public const string NotToldWarning =
        "Saved, but the household's agents could not be told just now: they see the change within a minute.";

    private static readonly AdminRefusal _nobody = new("There is nobody like that in the household.", []);

    private static readonly AdminRefusal _lastOwner = new(
        "The household's last owner stays an enabled owner: make someone else an owner first.", []);

    private static readonly AdminRefusal _operator = new(
        "The operator's account is the deployment's: they manage it with the accounts container's command.", []);

    public static void Map(RouteGroupBuilder household)
    {
        var members = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Members);
        var member = $"{members}/{{userId}}";

        household.MapGet(members, async (AdminCaller caller, MemberStore store, CancellationToken ct) =>
            Results.Ok(await AnswerAsync(store, caller, ct)));

        household.MapPost(AdminApi.Under(AdminProtocol.Household, AdminProtocol.MemberInvites), async (
            AdminCaller caller, [FromBody] MemberInvite? invite, HttpContext http, LinkStore links, AdminLog log,
            AccountsSettings settings, CancellationToken ct) =>
        {
            var name = invite?.DisplayName?.Trim() ?? "";
            var problems = new[]
            {
                name.Length is 0 or > Profile.DisplayNameLength
                    ? $"a display name is 1 to {Profile.DisplayNameLength} characters"
                    : null,
                RoleOf(invite?.Role) is null ? $"a role is {AdminMember.Owner} or {AdminMember.Member}" : null
            }.OfType<string>().ToList();
            if (problems.Count > 0)
            {
                return Results.BadRequest(new AdminRefusal("No invite was made.", problems));
            }

            var role = RoleOf(invite!.Role)!.Value;
            var issued = await links.MintInviteAsync(caller.HouseholdId, caller.UserId, name, role, ct);
            await log.RecordAsync(http, caller, $"Invited somebody as {(role == Role.Owner ? "an owner" : "a member")}", ct);
            var deepLink = settings.Telegram.DeepLink(issued.Token);
            return Results.Ok(new AdminSaved<MemberLink>(new MemberLink(
                issued.Url.ToString(), issued.ExpiresAt, deepLink, deepLink is null ? $"/start {issued.Token}" : null)));
        });

        household.MapPut(member, async (
            AdminCaller caller, string userId, [FromBody] MemberEdit? edit, HttpContext http, MemberStore store, AdminLog log,
            CancellationToken ct) =>
        {
            if (edit is null || edit is { Role: null, SeesAll: null })
            {
                return Results.BadRequest(new AdminRefusal("Nothing was changed: send a role, sees-all, or both.", []));
            }

            var role = edit.Role is null ? null : RoleOf(edit.Role);
            if (edit.Role is not null && role is null)
            {
                return Results.BadRequest(new AdminRefusal("Nothing was changed.", [$"a role is {AdminMember.Owner} or {AdminMember.Member}"]));
            }

            if (await FindAsync(store, caller, userId, ct) is not { } before)
            {
                return Results.NotFound(_nobody);
            }

            if (role is { } changedRole && await store.ChangeRoleAsync(caller.HouseholdId, userId, changedRole, ct) == RoleChange.LastOwner)
            {
                return Results.Conflict(_lastOwner);
            }

            if (edit.SeesAll is { } seesAll)
            {
                await store.SetSeesAllConversationsAsync(caller.HouseholdId, userId, seesAll, ct);
            }

            var after = (await FindAsync(store, caller, userId, ct))!;
            var changed = new (string Field, bool Changed)[]
            {
                ("role", before.Role != after.Role), ("sees all conversations", before.SeesAllConversations != after.SeesAllConversations)
            }.Where(f => f.Changed).Select(f => f.Field).ToList();
            await log.RecordAsync(http, caller,
                changed.Count == 0 ? $"Saved member {userId} unchanged" : $"Changed member {userId}'s {string.Join(", ", changed)}", ct);
            return Results.Ok(new AdminSaved<AdminMembers>(await AnswerAsync(store, caller, ct)));
        });

        household.MapPost($"{member}/disable", async (
            AdminCaller caller, string userId, HttpContext http, MemberStore store, AdminLog log, RedisAnnouncer announcer,
            CancellationToken ct) =>
        {
            switch (await store.DisableAsync(caller.HouseholdId, userId, ct))
            {
                case Disabling.NotFound:
                    return Results.NotFound(_nobody);
                case Disabling.Operator:
                    return Results.Json(_operator, statusCode: StatusCodes.Status403Forbidden);
                case Disabling.LastOwner:
                    return Results.Conflict(_lastOwner);
            }

            return await AnnouncedAsync(http, caller, store, log, announcer, $"Disabled member {userId}", ct);
        });

        household.MapPost($"{member}/enable", async (
            AdminCaller caller, string userId, HttpContext http, MemberStore store, AdminLog log, RedisAnnouncer announcer,
            CancellationToken ct) =>
            await store.EnableAsync(caller.HouseholdId, userId, ct)
                ? await AnnouncedAsync(http, caller, store, log, announcer, $"Re-enabled member {userId}", ct)
                : Results.NotFound(_nobody));

        // Not for the operator, as on the members page: their way back in is the container command,
        // and an owner who could mint them a link could take the deployment's dashboard. Not for a
        // disabled member either: their link would open nothing until they are re-enabled.
        household.MapPost($"{member}/recovery", async (
            AdminCaller caller, string userId, HttpContext http, MemberStore store, LinkStore links, AdminLog log,
            CancellationToken ct) =>
        {
            switch (await FindAsync(store, caller, userId, ct))
            {
                case null:
                    return Results.NotFound(_nobody);
                case { IsOperator: true }:
                    return Results.Json(_operator, statusCode: StatusCodes.Status403Forbidden);
                case { Disabled: true }:
                    return Results.Conflict(new AdminRefusal("A disabled member's link would open nothing: re-enable them first.", []));
            }

            var link = (await links.MintRecoveryAsync(caller.HouseholdId, caller.UserId, userId, ct))!;
            await log.RecordAsync(http, caller, $"Minted a recovery link for member {userId}", ct);
            return Results.Ok(new AdminSaved<MemberLink>(new MemberLink(link.Url.ToString(), link.ExpiresAt)));
        });
    }

    // The change stood whether or not the agent heard of it; it is recorded either way, and the owner
    // is told when the agent may take up to its view's lifetime to see it.
    private static async Task<IResult> AnnouncedAsync(
        HttpContext http, AdminCaller caller, MemberStore store, AdminLog log, RedisAnnouncer announcer, string summary,
        CancellationToken ct)
    {
        var notTold = await announcer.AnnounceAsync(HouseholdConfigProtocol.SavedChannel,
            new HouseholdConfigSaved(caller.HouseholdId, HouseholdConfigProtocol.PeopleChanged), ct);
        await log.RecordAsync(http, caller, summary, ct);
        return Results.Ok(new AdminSaved<AdminMembers>(await AnswerAsync(store, caller, ct), notTold is null ? null : NotToldWarning));
    }

    private static async Task<Member?> FindAsync(MemberStore store, AdminCaller caller, string userId, CancellationToken ct) =>
        (await store.ListAsync(caller.HouseholdId, ct)).FirstOrDefault(m => m.Id == userId);

    private static async Task<AdminMembers> AnswerAsync(MemberStore store, AdminCaller caller, CancellationToken ct) =>
        new([
            .. (await store.ListAsync(caller.HouseholdId, ct)).Select(m => new AdminMember(
                m.Id, m.DisplayName, m.Role == Role.Owner ? AdminMember.Owner : AdminMember.Member, m.IsOperator,
                m.SeesAllConversations, m.Disabled))
        ]);

    private static Role? RoleOf(string? role) => role switch
    {
        AdminMember.Owner => Role.Owner,
        AdminMember.Member => Role.Member,
        _ => null
    };
}