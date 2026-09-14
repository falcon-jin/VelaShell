using VelaShell.Core.FileTransfer.Model;
using VelaShell.Core.Models;
using VelaShell.Services.FileTransfer;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="FolderTransferFileSink" /> 的目录选择逻辑。
/// 取消一次即中止(2026-09-14 起不再重弹追问),选定后同会话不再弹第二次。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class FolderTransferFileSinkTests
{
    private static Func<Task<AppSettings>> Settings()
    {
        var settings = new AppSettings();
        settings.Transfer.LocalDownloadDirectory = Path.GetTempPath();
        settings.Transfer.ConflictPolicy = "rename";
        return () => Task.FromResult(settings);
    }

    private static TransferFileMetadata Meta(string name) => new() { FileName = name, Size = 10 };

    /// <summary>取消(picker 返回 null)应当只弹一次就中止,不再给第二次机会。</summary>
    [TestMethod]
    public async Task Cancel_AbortsWithoutReprompting()
    {
        var calls = new List<TransferFolderPromptRequest>();
        Task<string?> picker(TransferFolderPromptRequest req, CancellationToken _)
        {
            calls.Add(req);
            return Task.FromResult<string?>(null);
        }

        var sink = new FolderTransferFileSink(picker, Settings());
        (TransferFileDisposition disposition, _) =
            await sink.OnFileOfferedAsync(Meta("a.bin"), new FileTransferItem { FileName = "a.bin" }, CancellationToken.None);

        Assert.AreEqual(TransferFileDisposition.Abort, disposition);
        Assert.HasCount(1, calls, "取消就是取消,不该追问第二遍");
    }

    /// <summary>取消之后,同会话的后续文件也不再弹窗 —— 整笔会话已经判定中止。</summary>
    [TestMethod]
    public async Task CancelThenNextFile_StillAbortsWithoutPrompting()
    {
        int callCount = 0;
        Task<string?> picker(TransferFolderPromptRequest _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<string?>(null);
        }

        var sink = new FolderTransferFileSink(picker, Settings());
        await sink.OnFileOfferedAsync(Meta("a.bin"), new FileTransferItem { FileName = "a.bin" }, CancellationToken.None);
        (TransferFileDisposition disposition, _) =
            await sink.OnFileOfferedAsync(Meta("b.bin"), new FileTransferItem { FileName = "b.bin" }, CancellationToken.None);

        Assert.AreEqual(TransferFileDisposition.Abort, disposition);
        Assert.AreEqual(1, callCount, "目录选择的结果缓存在 sink 内,取消也算一次已解析");
    }

    /// <summary>选定目录:应接受该文件并写入所选目录。</summary>
    [TestMethod]
    public async Task Choose_AcceptsIntoChosenFolder()
    {
        string chosen = Path.Combine(Path.GetTempPath(), "vela-zmodem-choose-" + Guid.NewGuid().ToString("N"));
        int callCount = 0;
        Task<string?> picker(TransferFolderPromptRequest _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<string?>(chosen);
        }

        var sink = new FolderTransferFileSink(picker, Settings());
        var item = new FileTransferItem { FileName = "b.bin" };
        (TransferFileDisposition disposition, _) =
            await sink.OnFileOfferedAsync(Meta("b.bin"), item, CancellationToken.None);

        try
        {
            Assert.AreEqual(TransferFileDisposition.Accept, disposition);
            Assert.AreEqual(1, callCount);
            Assert.IsNotNull(item.LocalPath);
            Assert.IsTrue(item.LocalPath!.StartsWith(chosen, StringComparison.Ordinal));
        }
        finally
        {
            await sink.DisposeAsync();
            try { Directory.Delete(chosen, recursive: true); } catch { /* 清理测试目录,失败无碍 */ }
        }
    }

    /// <summary>目录一旦选定,同会话后续文件不再弹窗(选择缓存在 sink 内)。</summary>
    [TestMethod]
    public async Task FolderChosenOnce_NotPromptedAgainForLaterFiles()
    {
        string chosen = Path.Combine(Path.GetTempPath(), "vela-zmodem-once-" + Guid.NewGuid().ToString("N"));
        int callCount = 0;
        Task<string?> picker(TransferFolderPromptRequest _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<string?>(chosen);
        }

        var sink = new FolderTransferFileSink(picker, Settings());
        try
        {
            await sink.OnFileOfferedAsync(Meta("f1.bin"), new FileTransferItem { FileName = "f1.bin" }, CancellationToken.None);
            await sink.OnFileOfferedAsync(Meta("f2.bin"), new FileTransferItem { FileName = "f2.bin" }, CancellationToken.None);
            Assert.AreEqual(1, callCount, "同会话只应弹一次目录框");
        }
        finally
        {
            await sink.DisposeAsync();
            try { Directory.Delete(chosen, recursive: true); } catch { /* ignore */ }
        }
    }
}
