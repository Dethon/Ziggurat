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
    private static readonly AdminMember Ines = new("ines", "Inés Rivas", AdminMember.Owner, false, false, false);

    private static AdminMembers Members => new([Ines]);

    [Fact]
    public void ARefusedChange_PutsTheFieldsBackToWhatIsSaved()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(Members);
        drafts.For(Ines).Role = AdminMember.Member;

        drafts.Answered(new HouseholdMembersState
        {
            Members = Members, Notice = new AdminNotice(AdminNoticeKind.Refused, "The household's last owner stays an enabled owner.")
        });

        drafts.For(Ines).Role.ShouldBe(AdminMember.Owner);
    }

    [Fact]
    public void AnEditNothingAnswered_IsKept()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(Members);
        drafts.For(Ines).SeesAll = true;

        drafts.Answered(new HouseholdMembersState { Members = Members });

        drafts.For(Ines).SeesAll.ShouldBeTrue();
    }

    // Two owners on the members page: Ana made Carlos an owner, then Bea — whose page still said
    // Member — ticked "Sees all conversations" for him and saved. The page sent the role it had
    // loaded beside the tick, and Carlos was a member again with nobody told. A save says only what
    // this page changed, so another owner's change to the other field stands.
    private static readonly AdminMember Carlos = new("carlos", "Carlos", AdminMember.Member, false, false, false);

    [Fact]
    public void ASaveOfOneField_SendsThatFieldAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([Carlos]));
        drafts.For(Carlos).SeesAll = true;

        drafts.EditOf(Carlos).ShouldBe(new MemberEdit(null, true));
    }

    [Fact]
    public void ASaveOfTheRole_SendsTheRoleAlone()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([Carlos]));
        drafts.For(Carlos).Role = AdminMember.Owner;

        drafts.EditOf(Carlos).ShouldBe(new MemberEdit(AdminMember.Owner, null));
    }

    [Fact]
    public void AFieldChangedAndChangedBack_IsNotSent()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([Carlos]));
        drafts.For(Carlos).Role = AdminMember.Owner;
        drafts.For(Carlos).Role = AdminMember.Member;
        drafts.For(Carlos).SeesAll = true;

        drafts.EditOf(Carlos).ShouldBe(new MemberEdit(null, true));
    }

    [Fact]
    public void NothingChanged_IsNothingToSave()
    {
        var drafts = new MemberDrafts();
        drafts.Reset(new AdminMembers([Carlos]));
        drafts.For(Carlos).SeesAll = true;
        drafts.For(Carlos).SeesAll = false;

        drafts.EditOf(Carlos).ShouldBeNull();
    }
}