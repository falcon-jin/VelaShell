using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 删除(可能递归)的进度:已删除的条目数、预计删除的总条目数,以及最近一次被删除的路径。
/// </summary>
/// <param name="DeletedCount">已删除的条目数。</param>
/// <param name="TotalCount">预计删除的总条目数。</param>
/// <param name="CurrentPath">最近一次被删除的路径。</param>
// ReSharper disable once NotAccessedPositionalProperty.Global
public readonly record struct SftpDeleteProgress(int DeletedCount, int TotalCount, string CurrentPath)
{
    /// <summary>删除进度百分比,取值范围 [0, 100]。</summary>
    public int Percentage => TotalCount <= 0 ? 0 : (int)Math.Clamp(((double)DeletedCount * 100) / TotalCount, 0, 100);
}

/// <summary>
/// 基于已有 SSH 会话的 SFTP 文件操作:目录列举、上传/下载、删除、创建、重命名、权限与元数据查询,以会话 id 为键。
/// </summary>
public interface ISftpService : IAsyncDisposable
{
    /// <summary>列举远端目录的条目。</summary>
    Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>将本地文件上传到给定远端路径,并报告传输进度。</summary>
    Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>将远端文件下载到给定本地路径,并报告传输进度。</summary>
    Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, IProgress<TransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除文件,或递归删除目录及其全部内容。每移除一个条目回报一次进度,
    /// 以便界面对大/慢文件夹的删除展示进度。
    /// </summary>
    Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>在给定远端路径创建新目录。</summary>
    Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>在给定远端路径创建一个空文件。</summary>
    Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 若目录不存在则创建(幂等)。上传文件夹树时使用,以便重建已存在的子目录不会报错。
    /// </summary>
    Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>重命名或移动远端条目(SFTP 的 rename 兼具 move 语义)。</summary>
    Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将远端文件或目录树复制到同一服务器的另一路径。单个文件采用先下载到内存再上传的方式;
    /// 目录则逐文件递归复制。按文件回报传输进度。
    /// </summary>
    Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改远端条目的权限(chmod)。<paramref name="octalMode" /> 是三位八进制数字,
    /// 以十进制数书写(如 755、644),与 `chmod` 记法一致。
    /// </summary>
    Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把远端文件的修改时间设为 <paramref name="lastWriteTimeUtc" />(UTC)。目录同步靠它在上传后
    /// 把远端 mtime 对齐本地源文件,否则下一次比较会把刚传上去的文件判成「远端较新」。
    /// 后端没有这种能力(插件协议、不支持 <c>MFMT</c> 的 FTP 服务器)时抛 <see cref="NotSupportedException" />。
    /// </summary>
    Task SetLastWriteTimeAsync(Guid sessionId, string remotePath, DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在服务器端批量算文件的 SHA-256(小写十六进制),不经网络传内容。返回 请求的路径 → 摘要;
    /// 单个文件算不出来(不存在、没权限)时值为 null。整个后端做不到(没有 exec 通道 / 没有 <c>sha256sum</c>、
    /// FTP 服务器不提供 <c>HASH</c> / <c>XSHA256</c>、插件协议)时抛 <see cref="NotSupportedException" />,
    /// 调用方据此回退到大小与修改时间比较。
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> ComputeSha256Async(Guid sessionId, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在 <paramref name="linkPath" /> 创建指向 <paramref name="targetPath" /> 的符号链接。
    /// <paramref name="targetPath" /> 原样写入(相对路径按链接所在目录解析,与 <c>ln -s</c> 一致)。
    /// 后端没有这种能力(插件协议、不支持 <c>SITE SYMLINK</c> 的 FTP 服务器)时抛
    /// <see cref="NotSupportedException" />,而不是静默成功。
    /// </summary>
    Task CreateSymbolicLinkAsync(Guid sessionId, string linkPath, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>获取单个远端文件或目录的元数据。</summary>
    Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开远端文件的只读流(顺序读取,调用方负责释放)。适合边读边处理的大文件
    /// (日志、导出),不经本地临时文件中转。
    /// </summary>
    Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 远端路径是否存在(文件或目录)。用于在覆盖远端文件前进行上传冲突检测。
    /// </summary>
    Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话的 SFTP 工作目录(登录后即为账户的 home 目录),用于在该处打开浏览器而非文件系统根目录。
    /// </summary>
    Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关闭并释放某个会话的 SFTP 通道(在其 SSH 标签页关闭时调用),使其不再持有活动连接或接受操作。
    /// 若会话未打开 SFTP 通道则为空操作。
    /// </summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
