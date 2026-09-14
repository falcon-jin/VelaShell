using NSubstitute;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Ssh;
using VelaShell.Core.ZModem.Protocol;
using VelaShell.Terminal.FileTransfer;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 传输策略对路由器的门控:被禁用的协议必须<b>彻底不参与</b> —— 不嗅探、不武装、不接管。
/// </summary>
/// <remarks>
/// 留一份「检测到了但不接管」的隐藏状态是最糟的做法:用户明明关掉了 ZMODEM,屏幕上却还是
/// 少了一段字节,而任何解释都对不上他看到的设置。所以这里逐条钉住「关掉之后什么都不会发生」。
/// </remarks>
[TestClass]
[TestCategory("ZModem")]
public class TransferPolicyRouterTests
{
    /// <summary>远端 <c>sz</c> 注入的真 ZRQINIT 十六进制帧头。</summary>
    private static byte[] Zrqinit() =>
        ZModemFrameWriter.Write(ZModemHeader.Empty(ZModemFrameType.ZRQINIT), ZModemHeaderFormat.Hex);

    private static IShellStreamWrapper Shell()
    {
        IShellStreamWrapper shell = Substitute.For<IShellStreamWrapper>();
        shell.CanWrite.Returns(true);
        return shell;
    }

    private static TerminalTransferRouter Router(TerminalTransferPolicy policy) =>
        new(Shell(), () => new NullSink(), policy: policy);

    /// <summary>出厂默认策略下三种协议都可用,行为与本功能上线之前完全一致。</summary>
    [TestMethod]
    public void DefaultPolicy_AllowsEveryProtocol()
    {
        TerminalTransferPolicy policy = TerminalTransferPolicy.Default;

        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.ZModem));
        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.YModem));
        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.YModemG));
        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.XModem));
        Assert.IsTrue(policy.Allows(TerminalTransferProtocol.XModem1K));
    }

    /// <summary>ZMODEM 关掉后,引导帧头必须原样喂进终端,且不开会话。</summary>
    [TestMethod]
    public void ZModemDisabled_StartupHeaderReachesTerminalUntouched()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { ZModem = false });
        byte[] chunk = [.. "sz a.bin\r"u8.ToArray(), .. Zrqinit()];

        TransferRouteResult result = router.ProcessIncoming(chunk);

        Assert.IsFalse(result.SessionStarted);
        Assert.IsFalse(router.IsInSession);
        Assert.AreSequenceEqual(chunk, result.TerminalBytes, "关掉之后一个字节都不能被吞掉");
    }

    /// <summary>ZMODEM 关掉后,读循环的零拷贝快路径对含引导的块也应放行(检测器根本不跑)。</summary>
    [TestMethod]
    public void ZModemDisabled_FastPathPassesStartupHeader()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { ZModem = false });

        Assert.IsTrue(router.CanPassThrough(Zrqinit()));
    }

    /// <summary>对照组:ZMODEM 开着时同一块字节会触发接管。</summary>
    [TestMethod]
    public void ZModemEnabled_StartupHeaderStartsSession()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default);
        byte[] header = Zrqinit();

        Assert.IsFalse(router.CanPassThrough(header), "含引导的块必须走慢路径");
        TransferRouteResult result = router.ProcessIncoming(header);

        Assert.IsTrue(result.SessionStarted);
        Assert.IsTrue(router.IsInSession);
        router.CancelActiveSession();
    }

    /// <summary>被禁用的协议手动启动要被明确拒绝,而不是开一个注定跑不动的会话。</summary>
    [TestMethod]
    public void DisabledProtocol_ManualStartIsRejected()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { YModem = false });

        TransferStartFailure result =
            router.StartManualSession(TerminalTransferProtocol.YModem, FileTransferDirection.Receive);

        Assert.AreEqual(TransferStartFailure.ProtocolDisabled, result);
        Assert.IsFalse(router.IsInSession);
    }

    /// <summary>变体跟随父协议:关掉 XMODEM,XMODEM-1K 也一并关。</summary>
    [TestMethod]
    public void DisabledParentProtocol_AlsoRejectsItsVariant()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { XModem = false });

        Assert.AreEqual(
            TransferStartFailure.ProtocolDisabled,
            router.StartManualSession(TerminalTransferProtocol.XModem1K, FileTransferDirection.Receive));
    }

    /// <summary>被禁用的协议不再被命令行武装 —— 用户敲的 <c>sb</c> 照常交给远端,我们只是不接管。</summary>
    [TestMethod]
    public void DisabledProtocol_CommandLineDoesNotArm()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { YModem = false });

        Assert.IsFalse(router.NoteCommandSubmitted("sb a.bin"));
        Assert.IsFalse(router.HasPendingManualSession);
    }

    /// <summary>对照组:协议开着时同一条命令会武装一个待启动的会话。</summary>
    [TestMethod]
    public void EnabledProtocol_CommandLineArms()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default);

        Assert.IsTrue(router.NoteCommandSubmitted("sb a.bin"));
        Assert.IsTrue(router.HasPendingManualSession);
    }

    /// <summary>
    /// 设置页保存后的热更新:换策略即刻生效,不必重开连接。
    /// </summary>
    /// <remarks>
    /// 这正是「三种全关也照样装路由器」的理由 —— 不装的话,重新打开协议时没有东西能接住新策略。
    /// </remarks>
    [TestMethod]
    public void UpdatePolicy_TakesEffectWithoutReconnecting()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default with { ZModem = false });
        Assert.IsTrue(router.CanPassThrough(Zrqinit()), "关着的时候放行");

        router.UpdatePolicy(TerminalTransferPolicy.Default);

        Assert.IsFalse(router.CanPassThrough(Zrqinit()), "打开之后同一块字节改走慢路径");
        Assert.IsTrue(router.ProcessIncoming(Zrqinit()).SessionStarted);
        router.CancelActiveSession();
    }

    /// <summary>换策略不打断进行中的会话:传到一半时改设置,不该把这笔文件当场掐断。</summary>
    [TestMethod]
    public void UpdatePolicy_DoesNotInterruptARunningSession()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default);
        Assert.AreEqual(
            TransferStartFailure.None,
            router.StartManualSession(TerminalTransferProtocol.YModem, FileTransferDirection.Receive));

        router.UpdatePolicy(TerminalTransferPolicy.Default with { YModem = false });

        Assert.IsTrue(router.IsInSession, "已经跑起来的会话继续跑完");
        router.CancelActiveSession();
    }

    /// <summary>
    /// 回归:ZMODEM 关掉期间检测器一个字节都没吃到,它的跨分片尾部副本停在关掉那一刻 ——
    /// 重新打开时必须清掉,否则那段陈旧前缀会与新字节拼出一个「帧头」,把普通 shell 输出
    /// 当协议流交给引擎。
    /// </summary>
    [TestMethod]
    public void ReEnablingZModem_ClearsStaleDetectorCarry()
    {
        TerminalTransferRouter router = Router(TerminalTransferPolicy.Default);
        byte[] header = Zrqinit();

        // 先在开着的状态下喂进半个帧头,让检测器把它留作跨分片匹配的尾部副本。
        byte[] firstHalf = header[..(header.Length / 2)];
        Assert.IsFalse(router.ProcessIncoming(firstHalf).SessionStarted, "半个帧头还不该命中");

        // 关掉再打开:这中间的字节全从快路径过去,检测器完全没参与。
        router.UpdatePolicy(TerminalTransferPolicy.Default with { ZModem = false });
        router.UpdatePolicy(TerminalTransferPolicy.Default);

        // 补上后半截。若陈旧前缀还在,两半会拼成一个完整帧头而误触发。
        byte[] secondHalf = header[(header.Length / 2)..];
        TransferRouteResult result = router.ProcessIncoming(secondHalf);

        Assert.IsFalse(result.SessionStarted, "开关翻过一轮之后,上一段的半个帧头不该再参与拼接");
        Assert.IsFalse(router.IsInSession);
        Assert.AreSequenceEqual(secondHalf, result.TerminalBytes);
    }

    /// <summary>什么都不写入的接收端:这些用例只关心路由决策,不关心落地。</summary>
    private sealed class NullSink : IFileTransferSink
    {
        public ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
            TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken) =>
            ValueTask.FromResult((TransferFileDisposition.Accept, 0L));

        public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
