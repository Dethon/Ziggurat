using Domain.DTOs.Admin;
using Shouldly;
using WebChat.Client.Components.Admin;
using WebChat.Client.State.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// What a member's row shows in its fields: what the admin API answered, and the owner's edits in
// between. Inés, the household's only owner, made herself a member; the change was refused ("the
// household's last owner stays an enabled owner") and her row went on showing Member under the
// heading Owner, since nothing she was answered replaced her edit.
public class MemberDraftsTests
{
    private static readonly AdminMember _ines = new("ines", "Inés Rivas", AdminMember.Owner, false, false, false);

    private static AdminMembers Members => new([_ines]);

    [Fact]
    public void ARefusedChange_PutsTheFieldsBackToWhatIsSaved()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(Members);
        drafts.For(_ines).Role = AdminMember.Member;

        drafts.Answered(new HouseholdMembersState
        {
            Members = Members, Notice = new AdminNotice(AdminNoticeKind.Refused, "The household's last owner stays an enabled owner.")
        });

        drafts.For(_ines).Role.ShouldBe(AdminMember.Owner);
    }

    [Fact]
    public void AnEditNothingAnswered_IsKept()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(Members);
        drafts.For(_ines).SeesAll = true;

        drafts.Answered(new HouseholdMembersState { Members = Members });

        drafts.For(_ines).SeesAll.ShouldBeTrue();
    }

    // Two owners on the members page: Ana made Carlos an owner, then Bea — whose page still said
    // Member — ticked "Sees all conversations" for him and saved. The page sent the role it had
    // loaded beside the tick, and Carlos was a member again with nobody told. A save says only what
    // this page changed, so another owner's change to the other field stands.
    private static readonly AdminMember _carlos = new("carlos", "Carlos", AdminMember.Member, false, false, false);

    // Letting Carlos's agent read everyone's Sugary log is its own field, sent alone like the others.
    [Fact]
    public void ASugaryLogGrant_IsSentAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).ReadsSugaryLogs = true;

        drafts.EditOf(_carlos).ShouldBe(new MemberEdit(ReadsSugaryLogs: true));
    }

    // Turning Sugary on for Carlos is its own field too.
    [Fact]
    public void UsingSugary_IsSentAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).UsesSugary = true;

        drafts.EditOf(_carlos).ShouldBe(new MemberEdit(UsesSugary: true));
    }

    [Fact]
    public void ASaveOfOneField_SendsThatFieldAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).SeesAll = true;

        drafts.EditOf(_carlos).ShouldBe(new MemberEdit(null, true));
    }

    [Fact]
    public void ASaveOfTheRole_SendsTheRoleAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).Role = AdminMember.Owner;

        drafts.EditOf(_carlos).ShouldBe(new MemberEdit(AdminMember.Owner, null));
    }

    [Fact]
    public void AFieldChangedAndChangedBack_IsNotSent()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).Role = AdminMember.Owner;
        drafts.For(_carlos).Role = AdminMember.Member;
        drafts.For(_carlos).SeesAll = true;

        drafts.EditOf(_carlos).ShouldBe(new MemberEdit(null, true));
    }

    [Fact]
    public void NothingChanged_IsNothingToSave()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([_carlos]));
        drafts.For(_carlos).SeesAll = true;
        drafts.For(_carlos).SeesAll = false;

        drafts.EditOf(_carlos).ShouldBeNull();
    }
}