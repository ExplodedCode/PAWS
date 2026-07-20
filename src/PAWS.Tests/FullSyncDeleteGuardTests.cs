using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Pure coverage for <see cref="FullSyncService.ExceedsAutoDeleteThreshold"/> — the guard that holds an
/// auto-sync plan for manual review instead of applying it unattended. Verifies both the flat
/// MaxAutoDeletes(50) count and the proportion check ("more than half of the known tree", floored at a
/// minimum tree size so an ordinary one- or two-file deletion in a tiny folder isn't flagged just for
/// being "most" of it). Ported from PAWS.AuthTest's --deleteguardtest.
/// </summary>
public class FullSyncDeleteGuardTests
{
    [Theory]
    [InlineData(10, 100, false, "small batch, large tree")]
    [InlineData(51, 1000, true, "over the flat MaxAutoDeletes(50)")]
    [InlineData(6, 8, true, "small-folder wipe (6 of 8, 75%)")]
    [InlineData(1, 2, false, "tiny folder (1 of 2, below the size floor)")]
    [InlineData(3, 4, true, "at the size floor, over 50% (3 of 4)")]
    [InlineData(2, 4, false, "at the size floor, exactly 50% (2 of 4, strictly-greater-than)")]
    [InlineData(0, 0, false, "nothing known, nothing deleted")]
    public void ExceedsAutoDeleteThreshold_MatchesExpectedFlag(int deletes, int totalKnown, bool expected, string reason)
    {
        var actual = FullSyncService.ExceedsAutoDeleteThreshold(deletes, totalKnown);
        Assert.True(actual == expected, $"{reason}: expected {expected}, got {actual}");
    }
}
