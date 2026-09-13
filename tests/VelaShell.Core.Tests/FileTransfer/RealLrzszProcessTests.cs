using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.XYModem.Model;
using VelaShell.Core.XYModem.Protocol;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.FileTransfer;

/// <summary>
/// 我们的 Z/X/YMODEM 引擎对<b>真实 lrzsz 进程</b>的端到端互操作:经 WSL 起 rz/sz/rb/sb/rx/sx,
/// 用进程的 stdin/stdout 当链路。手工构造的帧字节只能证明「我们认为 lrzsz 会发什么」,
/// 这里证明的是「lrzsz 真的收下 / 发出了完整的文件」。
/// <para>
/// 默认跳过(Inconclusive)。启用:设 <c>VELASHELL_LRZSZ_WSL_BIN</c> 为 WSL 内 lrzsz 可执行文件所在目录
/// (如 <c>/usr/bin</c>),可选 <c>VELASHELL_LRZSZ_WSL_DISTRO</c> 指定发行版。
/// 免 root 获取:<c>apt-get download lrzsz &amp;&amp; dpkg -x lrzsz_*.deb root</c>。
/// </para>
/// </summary>
[TestClass]
[TestCategory("LrzszInterop")]
public class RealLrzszProcessTests
{
    private static string? BinDir => Environment.GetEnvironmentVariable("VELASHELL_LRZSZ_WSL_BIN");
    private static string? Distro => Environment.GetEnvironmentVariable("VELASHELL_LRZSZ_WSL_DISTRO");

    private static string RequireBin()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(BinDir))
        {
            Assert.Inconclusive("未设置 VELASHELL_LRZSZ_WSL_BIN(WSL 内 lrzsz 目录),跳过真机互操作。");
        }
        return BinDir!;
    }

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ZModem_UploadToRealRz_ArrivesIntact() =>
        await UploadAsync("rz -y", 300 * 1024, "zm-up.bin", pickerDelay: TimeSpan.Zero,
            (d, s) => new ZModemSender(d, s).SendAsync(CancellationToken.None));

    /// <summary>用户在文件选择框里磨蹭(rz 期间会重发 ZRINIT),陈旧帧不能把会话搅乱。</summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ZModem_UploadToRealRz_WithSlowFilePicker_ArrivesIntact() =>
        await UploadAsync("rz -y", 64 * 1024, "zm-slow.bin", pickerDelay: TimeSpan.FromSeconds(25),
            (d, s) => new ZModemSender(d, s).SendAsync(CancellationToken.None));

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ZModem_DownloadFromRealSz_ArrivesIntact() =>
        await DownloadAsync("sz", 300 * 1024, "zm-down.bin", TimeSpan.Zero,
            (d, s) => new ZModemReceiver(d, s).ReceiveAsync(CancellationToken.None));

    /// <summary>
    /// 真机日志(2026-09-13):保存目录框开了 4.3 秒,之后第一轮 ZDATA 整段没被识别,靠 ZEOF 长度校验才救回来。
    /// 这里复刻「询问保存目录时停留几秒」。
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ZModem_DownloadFromRealSz_WithSlowFolderPicker_ArrivesIntact() =>
        await DownloadAsync("sz", 3281, "zm-slow-down.bin", TimeSpan.FromSeconds(4.5),
            (d, s) => new ZModemReceiver(d, s).ReceiveAsync(CancellationToken.None));

    /// <summary>真机日志(2026-09-13):保存目录框开了 2.5 秒后,sb 下载在第 7 块处被我们发 CAN 中止。</summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_DownloadFromRealSb_WithSlowFolderPicker_ArrivesIntact() =>
        await DownloadAsync("sb", 3281, "ym-slow-down.bin", TimeSpan.FromSeconds(2.5),
            (d, s) => new XYModemReceiver(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .ReceiveAsync(CancellationToken.None));

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_UploadToRealRb_ArrivesIntact() =>
        await UploadAsync("rb -y", 100 * 1024 + 77, "ym-up.bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .SendAsync(CancellationToken.None));

    /// <summary>文件信息超过 128 字节时 0 号块须改用 1K 块,否则文件名被截断。</summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_UploadToRealRb_LongFileName_ArrivesIntact() =>
        await UploadAsync("rb -y", 5000, new string('n', 180) + ".bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .SendAsync(CancellationToken.None));

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_DownloadFromRealSb_ArrivesIntact() =>
        await DownloadAsync("sb", 100 * 1024 + 77, "ym-down.bin", TimeSpan.Zero,
            (d, s) => new XYModemReceiver(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .ReceiveAsync(CancellationToken.None));

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task XModem_UploadToRealRx_ArrivesIntact()
    {
        // 先过门:没配 WSL lrzsz 的环境(CI)必须跳过,而不是去跑 wsl.exe 然后红掉。
        RequireBin();
        // XMODEM 不传大小,末块 SUB 填充会留在对端文件里:按前缀比对,并确认填充全是 SUB。
        byte[] payload = RandomNumberGenerator.GetBytes(20_000);
        payload[^1] = 0x42; // 末字节不是 SUB,才能明确裁剪边界。
        string dir = await MakeRemoteDirAsync();
        var source = new InMemoryFileSource([("xm-up.bin", payload)]);
        await using var duplex = ProcessDuplex.Start(Wsl($"cd {dir} && {BinDir}/rx xm-up.bin"));

        FileTransferSession session = await new XYModemSender(
                duplex, source, new XYModemOptions { Protocol = TerminalTransferProtocol.XModem })
            .SendAsync(CancellationToken.None);
        await duplex.WaitForExitAsync();

        Assert.AreEqual(FileTransferState.Completed, session.Status, duplex.Stderr);
        byte[] remote = await ReadRemoteFileAsync($"{dir}/xm-up.bin");
        Assert.IsGreaterThanOrEqualTo(payload.Length, remote.Length);
        Assert.AreSequenceEqual(payload, remote[..payload.Length]);
        Assert.IsTrue(remote[payload.Length..].All(b => b == XYModemConstants.SUB));
    }

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task XModem_DownloadFromRealSx_ArrivesIntact()
    {
        RequireBin();
        byte[] payload = RandomNumberGenerator.GetBytes(20_000);
        payload[^1] = 0x42;
        string dir = await MakeRemoteDirAsync();
        await WriteRemoteFileAsync($"{dir}/xm-down.bin", payload);
        var sink = new InMemoryFileSink();
        await using var duplex = ProcessDuplex.Start(Wsl($"cd {dir} && {BinDir}/sx xm-down.bin"));

        FileTransferSession session = await new XYModemReceiver(
                duplex, sink, new XYModemOptions { Protocol = TerminalTransferProtocol.XModem })
            .ReceiveAsync(CancellationToken.None);
        await duplex.WaitForExitAsync();

        Assert.AreEqual(FileTransferState.Completed, session.Status, duplex.Stderr);
        Assert.AreSequenceEqual(payload, sink.Completed.Values.Single());
    }

    /// <summary>
    /// 真机日志(2026-09-13):经 SSH 的 PTY 跑 rx,收到第 1 块就发 CAN 退出(status=128),而管道上同样的上传是好的。
    /// 这里用 <c>script</c> 给 rx 一个真正的伪终端,复刻 SSH 会话里的 tty 行规程。块长取 128 的整数倍,免掉尾部 SUB 填充。
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task XModem_UploadToRealRx_OverPty_ArrivesIntact() =>
        await UploadAsync("rx xm-pty.bin", 128 * 100, "xm-pty.bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.XModem })
                .SendAsync(CancellationToken.None),
            pty: true);

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_UploadToRealRb_OverPty_ArrivesIntact() =>
        await UploadAsync("rb", 64 * 1024 + 5, "ym-pty.bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .SendAsync(CancellationToken.None),
            pty: true);

    /// <summary>
    /// 真机日志(2026-09-13)+ 本地复现:目标文件已存在且没加 <c>-y</c> 时,lrzsz 0.12.21rc 的 rb 应答 0 号块后
    /// 报 <c>free(): double free detected in tcache 2</c> 崩溃并发 CAN —— 这是 lrzsz 自身缺陷,不是我们发错了。
    /// 规避办法是 <c>rb -y</c>(覆盖):这里钉住它确实能把文件完整传上去。
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task YModem_UploadToRealRb_OverPty_TargetExists_WithOverwriteFlag_ArrivesIntact() =>
        await UploadAsync("rb -y", 5000, "ym-exists.bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.YModem })
                .SendAsync(CancellationToken.None),
            pty: true,
            targetExists: true);

    /// <summary>诊断:真机上 rx 两次都在第 1 块后发 CAN 退出(status=128),看目标已存在是否就是触发条件。</summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task XModem_UploadToRealRx_OverPty_TargetExists_Diagnostic() =>
        await UploadAsync("rx xm-exists.bin", 128 * 10, "xm-exists.bin", TimeSpan.Zero,
            (d, s) => new XYModemSender(d, s, new XYModemOptions { Protocol = TerminalTransferProtocol.XModem })
                .SendAsync(CancellationToken.None),
            pty: true,
            targetExists: true);

    private static async Task UploadAsync(
        string command,
        int size,
        string name,
        TimeSpan pickerDelay,
        Func<IByteDuplex, IFileTransferSource, Task<FileTransferSession>> run,
        bool pty = false,
        bool targetExists = false)
    {
        RequireBin();
        byte[] payload = RandomNumberGenerator.GetBytes(size);
        string dir = await MakeRemoteDirAsync();
        if (targetExists)
        {
            await WriteRemoteFileAsync($"{dir}/{name}", "old content"u8.ToArray());
        }
        IFileTransferSource source = new DelayedSource(new InMemoryFileSource([(name, payload)]), pickerDelay);
        string inner = $"cd {dir} && {BinDir}/{command}";
        // script 给子进程一个真伪终端(行规程、回显、流控都与 SSH 的 PTY 一致);-e 透传退出码,-f 及时刷出。
        await using var duplex = ProcessDuplex.Start(Wsl(pty ? $"script -qfec \"{inner}\" /dev/null" : inner));

        FileTransferSession session = await run(duplex, source);
        await duplex.WaitForExitAsync();

        Assert.AreEqual(FileTransferState.Completed, session.Status, $"stderr: {duplex.Stderr}");
        byte[] remote = await ReadRemoteFileAsync($"{dir}/{name}");
        int firstDiff = payload.AsSpan().CommonPrefixLength(remote);
        Assert.IsTrue(
            payload.AsSpan().SequenceEqual(remote),
            $"sent {payload.Length}B, remote {remote.Length}B, first diff @{firstDiff}; " +
            $"sent[..]={Convert.ToHexString(payload.AsSpan(firstDiff, Math.Min(12, payload.Length - firstDiff)))} " +
            $"remote[..]={Convert.ToHexString(remote.AsSpan(firstDiff, Math.Min(12, remote.Length - firstDiff)))}; stderr: {duplex.Stderr}");
    }

    private static async Task DownloadAsync(
        string tool,
        int size,
        string name,
        TimeSpan pickerDelay,
        Func<IByteDuplex, IFileTransferSink, Task<FileTransferSession>> run)
    {
        RequireBin();
        byte[] payload = RandomNumberGenerator.GetBytes(size);
        string dir = await MakeRemoteDirAsync();
        await WriteRemoteFileAsync($"{dir}/{name}", payload);
        var sink = new InMemoryFileSink();
        await using var duplex = ProcessDuplex.Start(Wsl($"cd {dir} && {BinDir}/{tool} {name}"));

        FileTransferSession session = await run(duplex, new DelayedSink(sink, pickerDelay));
        await duplex.WaitForExitAsync();

        Assert.AreEqual(FileTransferState.Completed, session.Status, $"stderr: {duplex.Stderr}");
        Assert.IsTrue(sink.Completed.TryGetValue(name, out byte[]? received), $"stderr: {duplex.Stderr}");
        Assert.AreSequenceEqual(payload, received);
    }

    private static ProcessStartInfo Wsl(string script)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(Distro))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(Distro);
        }
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return psi;
    }

    private static async Task<string> MakeRemoteDirAsync()
    {
        string dir = $"/tmp/velashell-lrzsz-{Guid.NewGuid():N}";
        await RunAsync($"mkdir -p {dir}", []);
        return dir;
    }

    private static async Task WriteRemoteFileAsync(string path, byte[] content) => await RunAsync($"cat > {path}", content);

    private static Task<byte[]> ReadRemoteFileAsync(string path) => RunAsync($"cat {path}", []);

    private static async Task<byte[]> RunAsync(string script, byte[] stdin)
    {
        using var process = Process.Start(Wsl(script))!;
        Task<byte[]> output = Task.Run(async () =>
        {
            using var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms);
            return ms.ToArray();
        });
        await process.StandardInput.BaseStream.WriteAsync(stdin);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, $"wsl `{script}` 失败:{await process.StandardError.ReadToEndAsync()}");
        return await output;
    }

    /// <summary>模拟用户在「选择保存目录」框里停留一段时间(对端此刻已在等我们的应答)。</summary>
    private sealed class DelayedSink(IFileTransferSink inner, TimeSpan delay) : IFileTransferSink
    {
        public async ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
            TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            return await inner.OnFileOfferedAsync(metadata, item, cancellationToken);
        }

        public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            inner.WriteAsync(item, data, cancellationToken);

        public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken) =>
            inner.CompleteAsync(item, cancellationToken);

        public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken) =>
            inner.FailAsync(item, error, cancellationToken);
    }

    /// <summary>模拟用户在文件选择框里停留一段时间。</summary>
    private sealed class DelayedSource(IFileTransferSource inner, TimeSpan delay) : IFileTransferSource
    {
        public async ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            return await inner.GetFilesAsync(cancellationToken);
        }

        public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(file, cancellationToken);
    }

    /// <summary>把子进程 stdin/stdout 适配成 <see cref="IByteDuplex" />(stdout 由后台泵搬进通道)。</summary>
    private sealed class ProcessDuplex : IByteDuplex
    {
        private readonly Process _process;
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Task _pump;
        private readonly Task<string> _stderr;

        private ProcessDuplex(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
            _pump = Task.Run(async () =>
            {
                byte[] buffer = new byte[16384];
                Stream stdout = process.StandardOutput.BaseStream;
                int n;
                while ((n = await stdout.ReadAsync(buffer)) > 0)
                {
                    // 走 PTY 时对端的 stderr(报错、崩溃信息)也混在这条流里,不记下来就只能看到一个 Failed。
                    VelaShell.Core.FileTransfer.Diagnostics.TransferTrace.LogBytes("PROC RX", buffer.AsSpan(0, n), max: 256);
                    _inbound.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
                }
                _inbound.Writer.TryComplete();
            });
        }

        public string Stderr => _stderr.IsCompleted ? _stderr.Result : "(running)";

        public bool HasPendingInbound => _inbound.Reader.Count > 0;

        public static ProcessDuplex Start(ProcessStartInfo psi) => new(Process.Start(psi)!);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(data, cancellationToken);
            }
            catch (IOException)
            {
                // 对端已退出:与 ShellStreamWrapper 一致,静默丢弃。
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
            catch (IOException)
            {
            }
        }

        public async Task WaitForExitAsync()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await _process.WaitForExitAsync(cts.Token);
                await _stderr;
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _pump.ConfigureAwait(false);
            _process.Dispose();
        }
    }
}
