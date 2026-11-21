namespace DiskGo.Core;

/// <summary>Where a running scan is, for a status line.</summary>
public enum ScanStage
{
    Preparing,
    ReadingMft,
    WalkingDirectories,
    BuildingTree,
    FindingDuplicates,
    Completed,
    Canceled,
}

/// <summary>A point-in-time view of a scan. Values only grow, so a stale report is still meaningful.</summary>
public readonly record struct ScanProgress(
    ScanStage Stage,
    long Files,
    long Folders,
    string Detail);
