using VelaShell.Core.DirectorySync;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public class DirectoryComparerTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 10, 30, 15, DateTimeKind.Utc);

    [TestMethod]
    public void Compare_ClassifiesEveryPairing()
    {
        SyncItem[] local =
        [
            File("same.txt", 10, T0),
            File("local-newer.txt", 10, T0.AddMinutes(5)),
            File("remote-newer.txt", 10, T0),
            File("size.txt", 10, T0),
            File("only-local.txt", 1, T0),
            Dir("dir"),
        ];
        SyncItem[] remote =
        [
            File("same.txt", 10, T0),
            File("local-newer.txt", 10, T0),
            File("remote-newer.txt", 10, T0.AddHours(1)),
            File("size.txt", 11, T0),
            File("only-remote.txt", 1, T0),
            Dir("dir"),
        ];

        var states = DirectoryComparer
            .Compare(local, remote, Options())
            .ToDictionary(c => c.RelativePath, c => c.State);

        Assert.AreEqual(SyncComparisonState.Same, states["same.txt"]);
        Assert.AreEqual(SyncComparisonState.LocalNewer, states["local-newer.txt"]);
        Assert.AreEqual(SyncComparisonState.RemoteNewer, states["remote-newer.txt"]);
        Assert.AreEqual(SyncComparisonState.Differs, states["size.txt"]);
        Assert.AreEqual(SyncComparisonState.LocalOnly, states["only-local.txt"]);
        Assert.AreEqual(SyncComparisonState.RemoteOnly, states["only-remote.txt"]);
        Assert.AreEqual(SyncComparisonState.Same, states["dir"]);
    }

    [TestMethod]
    public void Criteria_ControlWhatCountsAsDifferent()
    {
        SyncItem l = File("a.txt", 10, T0.AddHours(1));
        SyncItem r = File("a.txt", 20, T0);

        Assert.AreEqual(SyncComparisonState.LocalNewer, DirectoryComparer.CompareFiles(l, r, Options()));
        Assert.AreEqual(SyncComparisonState.Differs,
            DirectoryComparer.CompareFiles(l, r, Options() with { Criteria = SyncCriteria.Size }));
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, r, Options() with { Criteria = SyncCriteria.None }));
    }

    [TestMethod]
    public void MinutePrecisionRemote_DoesNotFlagEveryFileAsDifferent()
    {
        // FTP LIST 只给到分钟:本地 10:30:15 与远端 10:30(:00)是同一个时间。
        SyncItem l = File("a.txt", 10, T0);
        SyncItem r = File("a.txt", 10, T0.AddSeconds(-15)) with { Precision = SyncTimePrecision.Minute };
        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(l, r, Options()));

        // 同一精度下,整整晚一分钟就是较新。
        SyncItem later = r with { LastWriteTimeUtc = r.LastWriteTimeUtc.AddMinutes(1) };
        Assert.AreEqual(SyncComparisonState.RemoteNewer, DirectoryComparer.CompareFiles(l, later, Options()));
    }

    [TestMethod]
    public void OneSecondTolerance_AbsorbsFatGranularity_ButTwoSecondsDoNot()
    {
        SyncItem l = File("a.txt", 10, T0);
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddSeconds(1)), Options()));
        Assert.AreEqual(SyncComparisonState.RemoteNewer,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddSeconds(2)), Options()));
        Assert.AreEqual(SyncComparisonState.Same,
            DirectoryComparer.CompareFiles(l, File("a.txt", 10, T0.AddMilliseconds(900)), Options()), "亚秒级差异不算");
    }

    [TestMethod]
    public void UnknownRemoteTime_FallsBackToSizeOnly()
    {
        SyncItem l = File("a.txt", 10, T0);
        SyncItem r = File("a.txt", 10, DateTime.MinValue) with { Precision = SyncTimePrecision.Unknown };
        Assert.AreEqual(SyncComparisonState.Same, DirectoryComparer.CompareFiles(l, r, Options()));
    }

    [TestMethod]
    public void FileVersusDirectory_IsAConflict_AndNothingBelowItIsCompared()
    {
        SyncItem[] local = [File("x", 3, T0)];
        SyncItem[] remote = [Dir("x"), File("x/a.txt", 1, T0), File("x/b/c.txt", 1, T0)];

        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(local, remote, Options());

        Assert.HasCount(1, result, "冲突目录下的子项若被列成「只有远端有」,勾上删除就会去删整棵树");
        Assert.AreEqual(SyncComparisonState.Conflict, result[0].State);
    }

    [TestMethod]
    public void CaseInsensitiveLocal_TwoRemoteNamesDifferingOnlyInCase_AreAConflict()
    {
        SyncItem[] local = [File("readme.md", 1, T0)];
        SyncItem[] remote = [File("README.md", 1, T0), File("readme.md", 2, T0)];

        IReadOnlyList<SyncComparison> ignoreCase = DirectoryComparer.Compare(local, remote, Options() with { IgnoreCase = true });
        Assert.HasCount(1, ignoreCase);
        Assert.AreEqual(SyncComparisonState.Conflict, ignoreCase[0].State);

        IReadOnlyList<SyncComparison> caseSensitive = DirectoryComparer.Compare(local, remote, Options() with { IgnoreCase = false });
        Assert.HasCount(2, caseSensitive);
        Assert.AreEqual(SyncComparisonState.RemoteOnly, caseSensitive.Single(c => c.RelativePath == "README.md").State);
    }

    [TestMethod]
    public void IgnoreCase_PairsNamesThatDifferOnlyInCase()
    {
        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(
            [File("Photo.JPG", 5, T0)],
            [File("photo.jpg", 5, T0)],
            Options() with { IgnoreCase = true });

        Assert.HasCount(1, result);
        Assert.AreEqual(SyncComparisonState.Same, result[0].State);
    }

    [TestMethod]
    public void Results_ListParentsBeforeChildren()
    {
        IReadOnlyList<SyncComparison> result = DirectoryComparer.Compare(
            [File("a/b/c.txt", 1, T0), Dir("a/b"), Dir("a"), File("a-b.txt", 1, T0)],
            [],
            Options());

        List<string> order = [.. result.Select(c => c.RelativePath)];
        Assert.IsLessThan(order.IndexOf("a/b"), order.IndexOf("a"));
        Assert.IsLessThan(order.IndexOf("a/b/c.txt"), order.IndexOf("a/b"));
    }

    internal static SyncOptions Options() => new() { IgnoreCase = false };

    internal static SyncItem File(string path, long size, DateTime utc) => new(path, "/" + path, false, size, utc);

    internal static SyncItem Dir(string path) => new(path, "/" + path, true, 0, T0);
}
