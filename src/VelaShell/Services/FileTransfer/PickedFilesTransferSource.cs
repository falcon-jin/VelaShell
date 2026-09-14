using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Services.FileTransfer;

/// <summary>
/// 基于原生文件选择框的 ZMODEM 上传来源:远端跑 <c>rz</c> 时弹一次多选文件框,
/// 用户选中的文件按顺序发往远端。远端文件名只取纯文件名(不带本地目录),
/// 与 <c>sz</c> 的行为一致。<b>取消一次即放弃</b>(不再重弹追问),空清单会让发送方优雅收尾。
/// 由 <c>TerminalTransferRouter</c> 每会话经 sourceFactory 新建一个实例。
/// </summary>
internal sealed class PickedFilesTransferSource(
    Func<CancellationToken, Task<IReadOnlyList<string>>> pickFilesAsync) : IFileTransferSource
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _pickFilesAsync =
        pickFilesAsync ?? throw new ArgumentNullException(nameof(pickFilesAsync));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = await _pickFilesAsync(cancellationToken).ConfigureAwait(false);

        var files = new List<OutgoingTransferFile>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                FileInfo info = new(path);
                if (!info.Exists)
                {
                    continue;
                }
                files.Add(new(
                    info.FullName,
                    info.Name,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (Exception)
            {
                // 路径不可读(权限 / 已删除 / 被锁定):跳过,不拖垮整批。
                continue;
            }
        }
        return files;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // 引擎在 ZRPOS 续传时需要 Seek,故必须是可定位的文件流。
        Stream stream = new FileStream(
            file.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}
