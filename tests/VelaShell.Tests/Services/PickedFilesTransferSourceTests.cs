using VelaShell.Core.FileTransfer.Model;
using VelaShell.Services.FileTransfer;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="PickedFilesTransferSource" />(远端 <c>rz</c> 上传来源)的文件选择逻辑。
/// 与下载目录选择一致:取消一次即放弃(2026-09-14 起不再重弹追问)。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class PickedFilesTransferSourceTests
{
    /// <summary>取消(picker 返回空)应当只弹一次就放弃,空清单让发送方优雅收尾。</summary>
    [TestMethod]
    public async Task Cancel_GivesUpWithoutReprompting()
    {
        int callCount = 0;
        Task<IReadOnlyList<string>> picker(CancellationToken _)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var source = new PickedFilesTransferSource(picker);
        IReadOnlyList<OutgoingTransferFile> files = await source.GetFilesAsync(CancellationToken.None);

        Assert.IsEmpty(files, "取消应返回空清单(触发发送方优雅收尾)");
        Assert.AreEqual(1, callCount, "取消就是取消,不该追问第二遍");
    }

    /// <summary>选定文件:应返回该文件,远端名取纯文件名。</summary>
    [TestMethod]
    public async Task Choose_ReturnsChosenFiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "vela-zmodem-upload-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(tmp, [1, 2, 3, 4, 5]);
        int callCount = 0;
        Task<IReadOnlyList<string>> picker(CancellationToken _)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<string>>([tmp]);
        }

        try
        {
            var source = new PickedFilesTransferSource(picker);
            IReadOnlyList<OutgoingTransferFile> files = await source.GetFilesAsync(CancellationToken.None);

            Assert.AreEqual(1, callCount, "选定即返回,不该重弹");
            Assert.HasCount(1, files);
            Assert.AreEqual(Path.GetFileName(tmp), files[0].RemoteName, "远端文件名应为纯文件名");
            Assert.AreEqual(5, files[0].Size);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>选中的路径已不存在时跳过该项,不拖垮整批。</summary>
    [TestMethod]
    public async Task MissingPath_IsSkipped()
    {
        string missing = Path.Combine(Path.GetTempPath(), "vela-zmodem-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        Task<IReadOnlyList<string>> picker(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<string>>([missing]);

        var source = new PickedFilesTransferSource(picker);
        IReadOnlyList<OutgoingTransferFile> files = await source.GetFilesAsync(CancellationToken.None);

        Assert.IsEmpty(files);
    }
}
