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