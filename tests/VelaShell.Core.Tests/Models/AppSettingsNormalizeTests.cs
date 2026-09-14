using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 设置载入后的规整(<see cref="AppSettings.Normalize" />):把旧字段迁移到唯一权威字段。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public class AppSettingsNormalizeTests
{
    [TestMethod]
    public void VisualBell_IsMigratedToBellMode()
    {
        AppSettings settings = new();
        settings.TerminalBehavior.VisualBell = true;

        settings.Normalize();

        Assert.AreEqual("visual", settings.TerminalBehavior.BellMode);
        Assert.IsFalse(settings.TerminalBehavior.VisualBell);
    }

    /// <summary>
    /// 旧版把下载目录的默认值硬写成 "~/Downloads",Windows 上把"下载"文件夹改到别处的用户
    /// 因此永远拿不到自己指定的位置(#257)。存量配置里的这个字面默认值要迁成空串 = 跟随系统。
    /// </summary>
    [TestMethod]
    [DataRow("~/Downloads")]
    [DataRow("~\\Downloads")]
    [DataRow("  ~/Downloads  ")]
    public void LegacyDefaultDownloadDirectory_IsMigratedToFollowSystem(string legacy)
    {
        AppSettings settings = new();
        settings.Transfer.LocalDownloadDirectory = legacy;

        settings.Normalize();

        Assert.AreEqual(string.Empty, settings.Transfer.LocalDownloadDirectory);
        Assert.AreEqual(UserPathResolver.Downloads, UserPathResolver.ResolveOrDownloads(settings.Transfer.LocalDownloadDirectory));
    }

    [TestMethod]
    public void ExplicitDownloadDirectory_IsKept()
    {
        string chosen = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vela-dl"));
        AppSettings settings = new();
        settings.Transfer.LocalDownloadDirectory = chosen;

        settings.Normalize();

        Assert.AreEqual(chosen, settings.Transfer.LocalDownloadDirectory);
    }

    [TestMethod]
    public void DefaultDownloadDirectory_IsEmpty_MeaningFollowSystem() =>
        Assert.AreEqual(string.Empty, new AppSettings().Transfer.LocalDownloadDirectory);

    /// <summary>新装用户不该被默认打开录制:录制把终端原始输出整份落库,一开就是按 GB 长。</summary>
    [TestMethod]
    public void SessionRecording_IsOffByDefault() =>
        Assert.IsFalse(new AppSettings().Security.RecordProductionSessions);

    /// <summary>
    /// 存量配置里的 <c>true</c> 分不清是用户选的还是旧默认值带的(这开关早年默认开启且从未征求同意),
    /// 因此统一关一次。
    /// </summary>
    [TestMethod]
    public void LegacyRecordingDefault_IsTurnedOffOnce()
    {
        AppSettings settings = new();
        settings.Security.RecordProductionSessions = true;
        settings.Security.RecordingOptInMigrated = false;

        settings.Normalize();

        Assert.IsFalse(settings.Security.RecordProductionSessions);
        Assert.IsTrue(settings.Security.RecordingOptInMigrated, "迁移标记要落下,之后不再插手用户的选择");
    }

    /// <summary>迁移只做一次:用户此后自己打开录制,再次载入设置不能又给关掉。</summary>
    [TestMethod]
    public void RecordingOptIn_AfterMigration_IsRespected()
    {
        AppSettings settings = new();
        settings.Security.RecordProductionSessions = true;
        settings.Security.RecordingOptInMigrated = true;

        settings.Normalize();

        Assert.IsTrue(settings.Security.RecordProductionSessions);
    }

    /// <summary>
    /// 默认传输方式是个枚举,磁盘上的内容拦不住:手改坏的值要回落到 SFTP,
    /// 而不是让那对通用收发命令走进一个谁也认不出的分支。
    /// </summary>
    [TestMethod]
    public void UnknownTerminalDefaultMethod_FallsBackToSftp()
    {
        AppSettings settings = new();
        settings.Transfer.TerminalDefaultMethod = (TerminalTransferMethod)99;

        settings.Normalize();

        Assert.AreEqual(TerminalTransferMethod.Sftp, settings.Transfer.TerminalDefaultMethod);
    }

    /// <summary>用户明确选过的方式不能被规整改掉。</summary>
    [TestMethod]
    public void ValidTerminalDefaultMethod_IsKept()
    {
        AppSettings settings = new();
        settings.Transfer.TerminalDefaultMethod = TerminalTransferMethod.YModem;

        settings.Normalize();

        Assert.AreEqual(TerminalTransferMethod.YModem, settings.Transfer.TerminalDefaultMethod);
    }

    /// <summary>空白的上传命令等于注入一个空行,远端什么都不会发生;回落到出厂默认。</summary>
    [TestMethod]
    public void BlankZModemUploadCommand_FallsBackToDefault()
    {
        AppSettings settings = new();
        settings.Transfer.TerminalZModemUploadCommand = "  ";

        settings.Normalize();

        Assert.AreEqual(
            TerminalTransferPolicy.DefaultZModemUploadCommand,
            settings.Transfer.TerminalZModemUploadCommand);
    }

    /// <summary>XMODEM 块大小只有 128 与 1024 两个合法值,其余一律当 1K。</summary>
    [TestMethod]
    [DataRow(0, 1024)]
    [DataRow(64, 1024)]
    [DataRow(2048, 1024)]
    [DataRow(128, 128)]
    [DataRow(1024, 1024)]
    public void TerminalXModemBlockSize_IsSnappedToALegalValue(int stored, int expected)
    {
        AppSettings settings = new();
        settings.Transfer.TerminalXModemBlockSize = stored;

        settings.Normalize();

        Assert.AreEqual(expected, settings.Transfer.TerminalXModemBlockSize);
    }
}
