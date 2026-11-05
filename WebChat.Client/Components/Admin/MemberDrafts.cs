using Domain.DTOs.Admin;
using WebChat.Client.State.Admin;

namespace WebChat.Client.Components.Admin;

// The members page's fields: what the admin API answered — on opening and after every change — and
// the owner's edits in between. A refused change changed nothing, so the fields go back to what is
// saved rather than keep showing the edit under a row that says otherwise.
public sealed class MemberDrafts
{
    private Dictionary<string, MemberDraft> _drafts = [];

    public MemberDraft For(AdminMember member) =>
        _drafts.TryGetValue(member.Id, out var draft) ? draft : _drafts[member.Id] = MemberDraft.Of(member);

    // What this page changed and nothing else: the role and sees-all are saved together, and a field
    // sent as it was loaded would put back whatever another owner made it since (Ana made Carlos an
    // owner; Bea's page, still saying Member, ticked his sees-all and made him a member again).
    // Null when the page changed neither: there is nothing to save.
    public MemberEdit? EditOf(AdminMember member)
    {
        var draft = For(member);
        var edit = new MemberEdit(
            draft.Role == member.Role ? null : draft.Role,
            draft.SeesAll == member.SeesAll ? null : draft.SeesAll);
        return edit is { Role: null, SeesAll: null } ? null : edit;
    }

    public void Reset(AdminMembers members) => _drafts = members.Members.ToDictionary(m => m.Id, MemberDraft.Of);

    public void Answered(HouseholdMembersState state)
    {
        if (state is { Notice.Kind: AdminNoticeKind.Refused, Members: { } members })
        {
            Reset(members);
        }
    }
}

public sealed class MemberDraft
{
    public string Role { get; set; } = AdminMember.Member;
    public bool SeesAll { get; set; }

    public static MemberDraft Of(AdminMember member) => new() { Role = member.Role, SeesAll = member.SeesAll };
}