using Wisp.Core.Storage;
using Wisp.Core.Sync;

namespace Wisp.Core.Tests;

public class ReconcilerTests
{
    const uint Validity = 7;

    static LocalMessage Msg(long id, uint uid, MessageState state = MessageState.Normal, bool pending = false, uint validity = Validity) =>
        new(id, uid, validity, MessageFlags.None, state, null, pending);

    static HashSet<uint> Server(params uint[] uids) => [.. uids];

    [Fact]
    public void Message_missing_once_is_only_hidden_not_removed()
    {
        var plan = Reconciler.PlanAbsence([Msg(1, 10), Msg(2, 11)], Validity, Server(10));

        Assert.Equal([2L], plan.MarkMissing);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Message_missing_twice_is_removed()
    {
        var plan = Reconciler.PlanAbsence([Msg(1, 10), Msg(2, 11, MessageState.Missing)], Validity, Server(10));

        Assert.Equal([2L], plan.Remove);
        Assert.Empty(plan.MarkMissing);
    }

    [Fact]
    public void Missing_message_that_reappears_is_restored()
    {
        var plan = Reconciler.PlanAbsence([Msg(1, 10, MessageState.Missing)], Validity, Server(10));

        Assert.Equal([1L], plan.Restore);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Messages_with_pending_operations_are_never_touched()
    {
        var plan = Reconciler.PlanAbsence([Msg(1, 10, pending: true), Msg(2, 11, MessageState.Missing, pending: true)], Validity, Server());

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Messages_waiting_for_their_uid_are_never_touched()
    {
        var plan = Reconciler.PlanAbsence([Msg(1, 0)], Validity, Server());

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Empty_server_listing_for_a_full_folder_triggers_the_mass_delete_guard()
    {
        var local = Enumerable.Range(1, 100).Select(i => Msg(i, (uint)i)).ToList();

        var plan = Reconciler.PlanAbsence(local, Validity, Server());

        Assert.True(plan.MassDeleteGuardTriggered);
        Assert.Empty(plan.MarkMissing);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Mass_delete_goes_through_once_the_user_approved_it()
    {
        var local = Enumerable.Range(1, 100).Select(i => Msg(i, (uint)i)).ToList();

        var plan = Reconciler.PlanAbsence(local, Validity, Server(), massDeleteApproved: true);

        Assert.False(plan.MassDeleteGuardTriggered);
        Assert.Equal(100, plan.MarkMissing.Count);
    }

    [Fact]
    public void Deleting_a_few_messages_in_a_small_folder_is_not_suspicious()
    {
        var local = Enumerable.Range(1, 10).Select(i => Msg(i, (uint)i)).ToList();

        var plan = Reconciler.PlanAbsence(local, Validity, Server(1, 2, 3));

        Assert.False(plan.MassDeleteGuardTriggered);
        Assert.Equal(7, plan.MarkMissing.Count);
    }

    [Fact]
    public void Rows_from_an_old_uidvalidity_go_through_two_phases_as_well()
    {
        var plan = Reconciler.PlanAbsence(
            [Msg(1, 10, validity: 3), Msg(2, 11, MessageState.Missing, validity: 3)], Validity, Server(10, 11));

        Assert.Equal([1L], plan.MarkMissing);
        Assert.Equal([2L], plan.Remove);
    }
}
