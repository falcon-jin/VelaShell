using VelaShell.Core.DirectorySync;

namespace VelaShell.Core.Tests.DirectorySync;

[TestClass]
public class SyncFileMaskTests
{
    [TestMethod]
    public void EmptyMask_IncludesEverything()
    {
        Assert.IsTrue(SyncFileMask.TryParse("  ", out SyncFileMask mask, out string? error));
        Assert.IsNull(error);
        Assert.IsTrue(mask.IsEmpty);
        Assert.IsTrue(mask.Includes("a/b.txt", isDirectory: false));
        Assert.IsTrue(mask.Includes("a", isDirectory: true));
    }

    [TestMethod]
    public void FileIncludeMask_DoesNotKeepDirectoriesOut()
    {
        SyncFileMask mask = Parse("*.php");

        Assert.IsTrue(mask.Includes("src/index.php", isDirectory: false));
        Assert.IsFalse(mask.Includes("src/readme.md", isDirectory: false));
        // 目录不受文件包含掩码约束,否则 *.php 连 src/ 都进不去,子目录里的 php 永远同步不到。
        Assert.IsTrue(mask.Includes("src", isDirectory: true));
    }

    [TestMethod]
    public void ExcludeWinsOverInclude_AndDirectoryMasksOnlyApplyToDirectories()
    {
        SyncFileMask mask = Parse("*.log; *.txt | debug.log; node_modules/");

        Assert.IsTrue(mask.Includes("logs/app.log", isDirectory: false));
        Assert.IsFalse(mask.Includes("logs/debug.log", isDirectory: false));
        Assert.IsFalse(mask.Includes("web/node_modules", isDirectory: true));
        // 名叫 node_modules 的**文件**不被目录掩码排除。
        Assert.IsFalse(mask.Includes("node_modules", isDirectory: false), "不是 .log/.txt,被包含掩码挡掉");
        Assert.IsTrue(Parse("| node_modules/").Includes("node_modules", isDirectory: false));
    }

    [TestMethod]
    public void PathMasks_MatchAgainstTheRelativePath()
    {
        SyncFileMask mask = Parse("| src/obj/; /build/*.tmp");

        Assert.IsFalse(mask.Includes("src/obj", isDirectory: true));
        Assert.IsTrue(mask.Includes("lib/obj", isDirectory: true), "路径掩码不该按名字命中别处的 obj");
        Assert.IsFalse(mask.Includes("build/a.tmp", isDirectory: false));
        Assert.IsTrue(mask.Includes("other/a.tmp", isDirectory: false));
    }

    [TestMethod]
    public void Wildcards_AreCaseInsensitive_AndStarDotStarMatchesNamesWithoutExtension()
    {
        SyncFileMask mask = Parse("*.*");
        Assert.IsTrue(mask.Includes("Makefile", isDirectory: false));

        Assert.IsTrue(SyncFileMask.Wildcard("*.JPG", "photo.jpg"));
        Assert.IsTrue(SyncFileMask.Wildcard("file?.txt", "file1.txt"));
        Assert.IsFalse(SyncFileMask.Wildcard("file?.txt", "file12.txt"));
        Assert.IsTrue(SyncFileMask.Wildcard("a*b*c", "aXXbYYc"));
        Assert.IsFalse(SyncFileMask.Wildcard("a*b*c", "aXXbYY"));
        Assert.IsTrue(SyncFileMask.Wildcard("[x].txt", "[x].txt"), "方括号不是元字符");
    }

    [TestMethod]
    public void InvalidMasks_AreRejectedWithAReason()
    {
        Assert.IsFalse(SyncFileMask.TryParse("*.a | *.b | *.c", out _, out string? twoBars));
        Assert.IsNotNull(twoBars);
        Assert.IsFalse(SyncFileMask.TryParse("*.a; /", out _, out string? bareSlash));
        Assert.IsNotNull(bareSlash);
    }

    private static SyncFileMask Parse(string text)
    {
        Assert.IsTrue(SyncFileMask.TryParse(text, out SyncFileMask mask, out string? error), error);
        return mask;
    }
}
