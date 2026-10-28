using Wisp.Core.Storage;

namespace Wisp.Core.Sync;

public sealed record AbsencePlan(
    IReadOnlyList<long> MarkMissing,
    IReadOnlyList<long> Remove,
    IReadOnlyList<long> Restore,
    bool MassDeleteGuardTriggered)
{
    public static readonly AbsencePlan Empty = new([], [], [], false);
    public bool IsEmpty => MarkMissing.Count == 0 && Remove.Count == 0 && Restore.Count == 0;
}

/// <summary>
/// Pure decision logic for "which local messages are gone on the server". Kept free of I/O so the
/// safety rules from docs/SYNC.md can be tested directly.
/// </summary>
public static class Reconciler
{
    /// <summary>Below this many affected messages the mass-delete guard never triggers.</summary>
    public const int MassDeleteMinimum = 20;
    /// <summary>Fraction of the visible folder that counts as "suspiciously many".</summary>
    public const double MassDeleteFraction = 0.5;

    /// <summary>
    /// Compares the local cache with a <b>complete</b> UID listing of the folder.
    /// Callers must only pass a listing that finished successfully in a session where the folder had
    /// <paramref name="uidValidity"/>; a partial or failed listing must never reach this method.
    /// </summary>
    public static AbsencePlan PlanAbsence(
        IReadOnlyList<LocalMessage> local,
        uint uidValidity,
        IReadOnlySet<uint> serverUids,
        bool massDeleteApproved = false)
    {
        var markMissing = new List<long>();
        var remove = new List<long>();
        var restore = new List<long>();
        var visible = 0;

        foreach (var msg in local)
        {
            // Rows with queued work are owned by the operation queue, not by the listing.
            if (msg.HasPendingOps || msg.State == MessageState.Deleting) continue;

            if (msg.UidValidity != uidValidity)
            {
                // Left over from before a UIDVALIDITY change and not re-attached by Message-ID during the
                // full re-fetch. First pass: hide. Second pass: remove.
                if (msg.State == MessageState.Normal) markMissing.Add(msg.Id);
                else if (msg.State == MessageState.Missing) remove.Add(msg.Id);
                continue;
            }

            if (msg.Uid == 0) continue; // waiting for its UID (moved without UIDPLUS)
            if (msg.State == MessageState.Normal) visible++;

            var onServer = serverUids.Contains(msg.Uid);
            switch (msg.State)
            {
                case MessageState.Normal when !onServer: markMissing.Add(msg.Id); break;
                case MessageState.Missing when onServer: restore.Add(msg.Id); break;
                case MessageState.Missing when !onServer: remove.Add(msg.Id); break;
            }
        }

        var suspicious = markMissing.Count >= MassDeleteMinimum && markMissing.Count > visible * MassDeleteFraction;
        if (suspicious && !massDeleteApproved)
        {
            // Something looks wrong on the server side. Change nothing except un-hiding messages that came back.
            return new AbsencePlan([], [], restore, true);
        }
        return new AbsencePlan(markMissing, remove, restore, false);
    }
}
