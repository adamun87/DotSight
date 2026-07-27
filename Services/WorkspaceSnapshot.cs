using Microsoft.CodeAnalysis;

namespace DotSight.Services;

public sealed record WorkspaceSnapshotInfo(
    long Version,
    DateTime LoadedAtUtc,
    int TrackedInputs,
    string Source);

public sealed class WorkspaceSnapshot : IDisposable
{
    private Action? _release;

    internal WorkspaceSnapshot(
        Solution solution,
        WorkspaceSnapshotInfo info,
        Action release)
    {
        Solution = solution;
        Info = info;
        _release = release;
    }

    public Solution Solution { get; }

    public WorkspaceSnapshotInfo Info { get; }

    public void Dispose() =>
        Interlocked.Exchange(ref _release, null)?.Invoke();
}
