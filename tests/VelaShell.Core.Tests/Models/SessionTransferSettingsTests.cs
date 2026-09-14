using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 「全局终端内传输设置 + 会话级覆盖 + 有没有 SFTP 通道」合成生效策略的规则。
/// </summary>
/// <remarks>
/// 退回规则是这里唯一有分量的逻辑:用户选了 SFTP 却在串口上、或选了某个后来被关掉的协议,
/// 都不能让那对通用收发命令变成一条死路。三种协议全关时才停在 SFTP 上,由调用方置灰。
/// </remarks>
[TestClass]
public sealed class SessionTransferSettingsTests
{
    private static AppSettings Global() => new();

    private static SessionProfile Profile(TransferOverrides? overrides = null) =>
        new() { Name = "host", Transfer = overrides };

    [TestMethod]
    public void WithoutOverridesEverythingFollowsTheGlobalSettings()
    {
        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), Global(), hasSftp: true);

        Assert.IsTrue(policy.ZModem);
        Assert.IsTrue(policy.YModem);
        Assert.IsTrue(policy.XModem);
        Assert.AreEqual(TerminalTransferMethod.Sftp, policy.DefaultMethod, "出厂默认是走 SFTP。");
    }

    [TestMethod]
    public void NullProfileFollowsTheGlobalSettings()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalZModemEnabled = false;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(null, settings, hasSftp: false);

        Assert.IsFalse(policy.ZModem, "本地终端没有会话配置,三项一律跟随全局。");
        Assert.AreEqual(TerminalTransferMethod.YModem, policy.DefaultMethod, "没有 SFTP 又关了 ZMODEM,退到 YMODEM。");
    }

    [TestMethod]
    public void SessionOverrideBeatsTheGlobalSwitch()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalZModemEnabled = true;
        settings.Transfer.TerminalXModemEnabled = true;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(
            Profile(new() { ZModemEnabled = false, XModemEnabled = false }),
            settings,
            hasSftp: true);

        Assert.IsFalse(policy.ZModem);
        Assert.IsFalse(policy.XModem);
        Assert.IsTrue(policy.YModem, "没覆盖的那一项照常跟随全局。");
    }

    /// <summary>没有 SFTP 通道(串口 / Telnet / 本地终端)时,选中的 SFTP 要退回终端内协议。</summary>
    [TestMethod]
    public void SftpWithoutChannelFallsBackToTheBestEnabledProtocol()
    {
        AppSettings settings = Global();

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);

        Assert.AreEqual(TerminalTransferMethod.ZModem, policy.DefaultMethod, "退回顺序是 ZMODEM → YMODEM → XMODEM。");
    }

    [TestMethod]
    public void SftpWithoutChannelSkipsDisabledProtocolsWhenFallingBack()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalZModemEnabled = false;
        settings.Transfer.TerminalYModemEnabled = false;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);

        Assert.AreEqual(TerminalTransferMethod.XModem, policy.DefaultMethod);
    }

    /// <summary>选中的协议后来被关掉了:也要退回,否则那对命令指向一条永远走不通的路。</summary>
    [TestMethod]
    public void DisabledChosenProtocolFallsBackToo()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalDefaultMethod = TerminalTransferMethod.XModem;
        settings.Transfer.TerminalXModemEnabled = false;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);

        Assert.AreEqual(TerminalTransferMethod.ZModem, policy.DefaultMethod);
    }

    /// <summary>一种协议都不剩时停在 SFTP 上;配上 hasSftp = false,调用方据此置灰。</summary>
    [TestMethod]
    public void AllProtocolsDisabledLeavesSftpAndNoProtocol()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalZModemEnabled = false;
        settings.Transfer.TerminalYModemEnabled = false;
        settings.Transfer.TerminalXModemEnabled = false;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);

        Assert.AreEqual(TerminalTransferMethod.Sftp, policy.DefaultMethod);
        Assert.IsFalse(policy.ZModem);
        Assert.IsFalse(policy.YModem);
        Assert.IsFalse(policy.XModem);
        Assert.IsNull(policy.DefaultProtocol(), "SFTP 不对应任何终端内协议变体。");
    }

    /// <summary>会话级选中的方式优先于全局选中的方式。</summary>
    [TestMethod]
    public void SessionOverrideChoosesTheMethod()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalDefaultMethod = TerminalTransferMethod.Sftp;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(
            Profile(new() { DefaultMethod = TerminalTransferMethod.YModem }),
            settings,
            hasSftp: true);

        Assert.AreEqual(TerminalTransferMethod.YModem, policy.DefaultMethod);
        Assert.AreEqual(TerminalTransferProtocol.YModem, policy.DefaultProtocol());
    }

    [TestMethod]
    public void XModemBlockSizePicksTheVariantForSending()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalDefaultMethod = TerminalTransferMethod.XModem;

        TerminalTransferPolicy oneK = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);
        Assert.AreEqual(TerminalTransferProtocol.XModem1K, oneK.DefaultProtocol(), "默认 1024 字节即 XMODEM-1K。");

        settings.Transfer.TerminalXModemBlockSize = 128;
        TerminalTransferPolicy classic = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: false);
        Assert.AreEqual(TerminalTransferProtocol.XModem, classic.DefaultProtocol());
    }

    [TestMethod]
    public void BlankUploadCommandFallsBackToTheFactoryDefault()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalZModemUploadCommand = "   ";

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: true);

        Assert.AreEqual(TerminalTransferPolicy.DefaultZModemUploadCommand, policy.ZModemUploadCommand);
    }

    /// <summary>变体跟随各自的父协议,不单独开关。</summary>
    [TestMethod]
    public void VariantsFollowTheirParentProtocol()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalYModemEnabled = false;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(Profile(), settings, hasSftp: true);

        Assert.IsFalse(policy.Allows(TerminalTransferProtocol.YModem));
        Assert.IsFalse(policy.Allows(TerminalTransferProtocol.YModemG), "YMODEM-G 是 YMODEM 的流式变体。");
        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.XModem1K), "XMODEM-1K 跟着 XMODEM 走,没被关。");
    }

    /// <summary>
    /// 会话配置里一个认不出来的传输方式(手改过的文件、更高版本写下的值)必须退回全局值,
    /// 而不是被当成「不可用」一路退到终端内协议 —— 那会让一条 SFTP 通道好好的连接改走 ZMODEM。
    /// </summary>
    [TestMethod]
    public void UnknownOverrideMethodFallsBackToTheGlobalChoice()
    {
        AppSettings settings = Global();
        settings.Transfer.TerminalDefaultMethod = TerminalTransferMethod.Sftp;

        TerminalTransferPolicy policy = SessionTransferSettings.Resolve(
            Profile(new() { DefaultMethod = (TerminalTransferMethod)99 }),
            settings,
            hasSftp: true);

        Assert.AreEqual(TerminalTransferMethod.Sftp, policy.DefaultMethod);
    }

    /// <summary>四项全为 null 的覆盖对象等价于没有覆盖,界面据此存回 null。</summary>
    [TestMethod]
    public void EmptyOverridesAreEquivalentToNone()
    {
        TransferOverrides empty = new();
        Assert.IsTrue(empty.IsEmpty);

        TransferOverrides one = new() { ZModemEnabled = false };
        Assert.IsFalse(one.IsEmpty);

        TransferOverrides copy = one.Clone();
        Assert.AreEqual(one.ZModemEnabled, copy.ZModemEnabled);
        Assert.IsFalse(ReferenceEquals(one, copy));
    }
}
