using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>持有一个独立 SFTP 文档的全部状态与生命周期资源。</summary>
public sealed class SftpDocumentViewModel : ReactiveObject, IAsyncDisposable
{
    private readonly Func<Guid, CancellationToken, Task> _disconnectAsync;
    private readonly SerializedSftpService _serializedSftp;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _closeTask;
    private readonly Lock _closeSync = new();

    /// <summary>初始化 SFTP 文档视图模型,并开始加载本地/远程文件树。</summary>
    /// <param name="profile">用于建立连接的会话配置。</param>
    /// <param name="session">已建立的 SSH 会话。</param>
    /// <param name="workflow">连接工作流服务,关闭文档时用它断开会话。</param>
    /// <param name="sftpService">底层 SFTP 服务,构造时会被包成本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项快照。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">
    /// 解析「设置 → 文件传输 → 默认编辑器」的回调。独立 SFTP 标签与终端侧边栏的文件浏览器
    /// 是两个各自 new 出来的 <see cref="FileBrowserViewModel" />,宿主回调不会自动继承——
    /// 漏传这一个会让右键「使用默认编辑器打开」误报“未配置”(明明已配置)。
    /// </param>
    public SftpDocumentViewModel(
        SessionProfile profile,
        SshSession session,
        IConnectionWorkflowService workflow,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null)
        : this(profile,
               (session ?? throw new ArgumentNullException(nameof(session))).SessionId,
               (workflow ?? throw new ArgumentNullException(nameof(workflow))).DisconnectAsync,
               sftpService,
               transferOptions,
               transferSink,
               getDefaultEditorPath)
    {
        Session = session;
        // 标签页状态灯读的是 Status,而它的真值住在 SshSession 上 —— 转发一次属性变更,
        // 灯才会跟着会话断开/重连改色。FTP 走另一个构造函数,没有这条线。
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    /// <summary>
    /// 以「一个会话标识 + 一个断开回调」构造文档,不绑定具体协议。
    /// FTP 与 S3 会话都走这条:它们没有 <see cref="SshSession" />,也不经
    /// <see cref="IConnectionWorkflowService" />(那是 SSH 握手),但文件面板与传输栈完全共用
    /// —— 它们只认 <see cref="ISftpService" /> + 会话标识。
    /// </summary>
    /// <param name="profile">用于建立连接的会话配置。</param>
    /// <param name="sessionId">已建立的会话标识。</param>
    /// <param name="disconnectAsync">关闭文档时断开该会话的回调。</param>
    /// <param name="sftpService">底层远程文件服务,构造时会被包成本文档独占的串行化视图。</param>
    /// <param name="transferOptions">设置 → 文件传输 的选项快照。</param>
    /// <param name="transferSink">承载本文档所发起传输的浮动传输组件;为 null 时不上报进度。</param>
    /// <param name="getDefaultEditorPath">解析「默认编辑器」的回调。</param>
    public SftpDocumentViewModel(
        SessionProfile profile,
        Guid sessionId,
        Func<Guid, CancellationToken, Task> disconnectAsync,
        ISftpService sftpService,
        TransferOptions transferOptions,
        FileTransferViewModel? transferSink = null,
        Func<Task<string?>>? getDefaultEditorPath = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _disconnectAsync = disconnectAsync ?? throw new ArgumentNullException(nameof(disconnectAsync));
        _serializedSftp = new SerializedSftpService(sftpService ?? throw new ArgumentNullException(nameof(sftpService)), sessionId);
        SessionId = sessionId;
        Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        RemoteFiles = new FileBrowserViewModel(_serializedSftp, SessionId)
        {
            ServerDisplayName = Title,
            TransferSink = transferSink,
            TransferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions)),
            IsVisible = true,
            IsDragEnabled = true,
            GetDefaultEditorPath = getDefaultEditorPath,
            // 连接配置里的「默认打开路径」(FTP / FTPS 的高级选项)。其余协议没有这块设置,
            // 取到 null 即照旧从登录工作目录起步。
            InitialRemotePath = profile.Ftp?.InitialRemotePath,
        };
        LocalFiles = new LocalFilePaneViewModel(transferOptions)
        {
            UploadSelectedAsync = () => UploadSelectedAsync()
        };
        RemoteFiles.PickFolderForDownload = () => Task.FromResult<string?>(LocalFiles.CurrentPath);
        UploadSelectedCommand = ReactiveCommand.CreateFromTask(UploadSelectedAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(DownloadSelectedAsync);
        DeleteLocalSelectedCommand = LocalFiles.DeleteSelectedCommand;
        LocalFiles.ConfirmDelete = message => ConfirmLocalDelete?.Invoke(message) ?? Task.FromResult(false);
        CompareDirectoriesCommand = ReactiveCommand.CreateFromTask(CompareDirectoriesAsync);
        OpenSyncCommand = ReactiveCommand.CreateFromTask(OpenSyncAsync);
        LocalFiles.PropertyChanged += OnPanePropertyChanged;
        RemoteFiles.PropertyChanged += OnPanePropertyChanged;
        InitialLoadTask = LoadAsync();
    }

    /// <summary>「比较目录」:把两栏当前目录里不同的条目在各自一栏里选中(不递归,WinSCP 同名命令的口径)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareDirectoriesCommand { get; }

    /// <summary>打开目录同步窗口(同一文档只开一个,再点就把它提到前面)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSyncCommand { get; }

    /// <summary>上次「比较目录」的结论;任一栏换了目录就清掉,免得它描述的已经不是眼前这两个目录。</summary>
    public string? CompareSummary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>本文档里上次用过的同步选项。</summary>
    public DirectorySyncSettings SyncSettings { get; } = new();

    /// <summary>当前开着的同步窗口的视图模型;没开时为 null。</summary>
    public DirectorySyncViewModel? ActiveSync { get; private set; }

    /// <summary>由视图设置:显示(或激活)同步窗口。</summary>
    public Func<DirectorySyncViewModel, Task>? ShowSyncWindow { get; set; }

    /// <summary>
    /// 远端时间是否可能粗于秒。FTP 的 LIST 只到分钟、插件协议没有保证;SFTP 的 mtime 恒为秒级,
    /// 不能被一个恰好落在整分上的时间降级成「只精确到分钟」。
    /// </summary>
    internal bool RemoteTimesMayBeCoarse => Profile.ConnectionType is not (ConnectionType.SFTP or ConnectionType.SSH);

    /// <summary>「比较目录」与同步窗口共用的摘要缓存。</summary>
    private readonly SyncChecksumCache _checksumCache = new();

    /// <summary>
    /// 比较两栏当前目录(一层),选中各自一侧较新或独有的条目,并给出一句结论。
    /// 勾着 SHA-256 时(默认)大小相同的文件先比摘要,算不出来的回退到大小与修改时间。
    /// </summary>
    public async Task CompareDirectoriesAsync()
    {
        var options = new SyncOptions
        {
            Criteria = SyncSettings.Criteria == SyncCriteria.None ? SyncCriteria.Time | SyncCriteria.Size : SyncSettings.Criteria,
        };
        string localPath = LocalFiles.CurrentPath;
        string remotePath = RemoteFiles.CurrentPath;
        // 远端栏隐藏了点文件时,本地栏的点文件也不参与 —— 否则每个 .git、.env 都会被标成「只有本地有」。
        bool includeHidden = RemoteFiles.ShowHiddenFiles;
        LocalFileEntry[] localEntries = [.. LocalFiles.Entries.Where(e => !e.IsParentEntry && (includeHidden || !e.Name.StartsWith('.')))];
        RemoteFileInfoViewModel[] remoteEntries = [.. RemoteFiles.Files.Where(static f => !f.IsParentEntry && !f.IsDirectoryLink)];
        bool coarse = RemoteTimesMayBeCoarse;
        IReadOnlyList<SyncItem> localItems = [.. localEntries.Select(static e => new SyncItem(
            e.Name, e.FullPath, e.IsDirectory, e.IsDirectory ? 0 : e.SizeBytes, SyncTime.ToUtc(e.LastModified)))];
        IReadOnlyList<SyncItem> remoteItems = [.. remoteEntries.Select(f => new SyncItem(
            f.Name, f.FullPath, f.IsDirectory, f.IsDirectory ? 0 : f.SizeBytes, SyncTime.ToUtc(f.LastModified),
            coarse ? SyncTime.InferPrecision(f.LastModified) : SyncTimePrecision.Second))];

        string? fellBack = null;
        if (options.Criteria.HasFlag(SyncCriteria.Checksum))
        {
            CompareSummary = Strings.Get("Sync_CompareHashing");
            SyncChecksumOutcome outcome;
            try
            {
                outcome = await SyncChecksums.ApplyAsync(
                    localItems, remoteItems, options, _serializedSftp, SessionId, _checksumCache, cancellationToken: _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                CompareSummary = null;
                return;
            }
            // 算摘要期间用户换了目录:结论描述的已经不是眼前这两个目录,选中也会落到错的行上。
            if (!string.Equals(localPath, LocalFiles.CurrentPath, StringComparison.Ordinal)
                || !string.Equals(remotePath, RemoteFiles.CurrentPath, StringComparison.Ordinal))
            {
                CompareSummary = null;
                return;
            }
            localItems = outcome.Local;
            remoteItems = outcome.Remote;
            if (outcome.RemoteFailure is not null || outcome.FellBack > 0)
            {
                fellBack = Strings.Get("Sync_CompareFellBack");
            }
        }
        IReadOnlyList<SyncComparison> comparisons = DirectoryComparer.Compare(localItems, remoteItems, options);

        StringComparer comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var localMarked = new HashSet<string>(comparer);
        var remoteMarked = new HashSet<string>(comparer);
        int identical = 0;
        foreach (SyncComparison comparison in comparisons)
        {
            switch (comparison.State)
            {
                case SyncComparisonState.LocalOnly or SyncComparisonState.LocalNewer:
                    localMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.RemoteOnly or SyncComparisonState.RemoteNewer:
                    remoteMarked.Add(comparison.RelativePath);
                    break;
                case SyncComparisonState.Differs or SyncComparisonState.Conflict:
                    localMarked.Add(comparison.RelativePath);
                    remoteMarked.Add(comparison.RelativePath);
                    break;
                default:
                    identical++;
                    break;
            }
        }

        LocalFiles.SelectedEntries.Clear();
        foreach (LocalFileEntry entry in localEntries.Where(e => localMarked.Contains(e.Name)))
        {
            LocalFiles.SelectedEntries.Add(entry);
        }
        RemoteFiles.SelectedFiles.Clear();
        foreach (RemoteFileInfoViewModel entry in remoteEntries.Where(f => remoteMarked.Contains(f.Name)))
        {
            RemoteFiles.SelectedFiles.Add(entry);
        }
        string summary = localMarked.Count + remoteMarked.Count == 0
            ? Strings.Format("Sync_CompareIdentical", identical)
            : Strings.Format("Sync_CompareSummary", localMarked.Count, remoteMarked.Count, identical);
        CompareSummary = fellBack is null ? summary : summary + " " + fellBack;
    }

    private async Task OpenSyncAsync()
    {
        if (ShowSyncWindow is null)
        {
            return;
        }
        if (ActiveSync is null)
        {
            var sync = new DirectorySyncViewModel(
                _serializedSftp,
                SessionId,
                RemoteFiles,
                SyncSettings,
                LocalFiles.CurrentPath,
                RemoteFiles.CurrentPath,
                RemoteTimesMayBeCoarse,
                Title)
            {
                RefreshPanesAsync = RefreshPanesAsync,
                ChecksumCache = _checksumCache,
            };
            sync.Disposed += OnSyncDisposed;
            ActiveSync = sync;
        }
        await ShowSyncWindow(ActiveSync);
    }

    private void OnSyncDisposed(object? sender, EventArgs e)
    {
        if (sender is DirectorySyncViewModel sync)
        {
            sync.Disposed -= OnSyncDisposed;
            if (ReferenceEquals(ActiveSync, sync))
            {
                ActiveSync = null;
            }
        }
    }

    private async Task RefreshPanesAsync()
    {
        await LocalFiles.RefreshAsync(_lifetime.Token);
        await RemoteFiles.RefreshSilentlyAsync();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalFilePaneViewModel.CurrentPath) or nameof(FileBrowserViewModel.CurrentPath))
        {
            CompareSummary = null;
        }
    }

    /// <summary>用于建立连接的会话配置。</summary>
    public SessionProfile Profile { get; }
    /// <summary>当前活跃的 SSH 会话;FTP / S3 文档没有 SSH 会话,为 null。</summary>
    public SshSession? Session { get; }

    /// <summary>
    /// 标签页状态灯的连接状态。必须由本视图模型给出,不能让界面直接绑 <c>Session.Status</c>:
    /// FTP / S3 文档的 <see cref="Session" /> 是 null,那样每开一个这类标签都会刷一条
    /// <c>An error occurred binding 'Fill' to 'ViewModel.Session.Status': 'Value is null.'</c>,
    /// 而且那颗灯自始至终不上色。
    /// </summary>
    /// <remarks>
    /// 这两种协议都没有长驻的会话对象可读,而文档只在登录成功后才建出来,因此按「已连接」呈现;
    /// 掉线由文件面板的错误提示承担,侧边栏树上的状态圆点则由各自的
    /// <c>SessionStateChanged</c> 事件驱动(见 <c>MainWindowViewModel</c>)。
    /// </remarks>
    public SessionStatus Status => Session?.Status ?? SessionStatus.Connected;
    /// <summary>SSH 会话的唯一标识。</summary>
    public Guid SessionId { get; }
    /// <summary>SFTP 文档标签页的显示标题。</summary>
    public string Title { get; }
    /// <summary>本地文件浏览器面板。</summary>
    public LocalFilePaneViewModel LocalFiles { get; }
    /// <summary>远程文件浏览器面板。</summary>
    public FileBrowserViewModel RemoteFiles { get; }
    /// <summary>将选中的本地文件上传到远程服务器的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> UploadSelectedCommand { get; }
    /// <summary>将选中的远程文件下载到本机的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DownloadSelectedCommand { get; }
    /// <summary>删除选中的本地文件的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteLocalSelectedCommand { get; }
    /// <summary>确认本地文件删除的回调。</summary>
    public Func<string, Task<bool>>? ConfirmLocalDelete { get; set; }

    internal Task InitialLoadTask { get; }

    /// <summary>将选中的本地文件上传到远程服务器。</summary>
    public async Task UploadSelectedAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = [.. LocalFiles.SelectedEntries
            .Where(entry => !entry.IsParentEntry)
            .Select(entry => entry.FullPath)];
        await RemoteFiles.UploadLocalPathsAsync(paths, cancellationToken);
    }

    /// <summary>将选中的远程文件下载到本机。</summary>
    public async Task DownloadSelectedAsync(CancellationToken cancellationToken = default)
    {
        RemoteFileInfoViewModel[] entries = [.. RemoteFiles.SelectedFiles.Where(entry => !entry.IsParentEntry)];
        if (entries.Length == 0 || string.IsNullOrWhiteSpace(LocalFiles.CurrentPath))
        {
            return;
        }
        await RemoteFiles.DownloadRemoteEntriesAsync(entries, LocalFiles.CurrentPath, cancellationToken);
        if (RemoteFiles.ErrorMessage is null)
        {
            await LocalFiles.RefreshAsync(cancellationToken);
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshSession.Status) or null)
        {
            this.RaisePropertyChanged(nameof(Status));
        }
    }

    /// <summary>分离文件浏览器并取消生命周期令牌。</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        // 同步窗口与「保持远端最新」的监视都挂在这条连接上:连接要走了,它们得先停。
        ActiveSync?.Dispose();
        LocalFiles.PropertyChanged -= OnPanePropertyChanged;
        RemoteFiles.PropertyChanged -= OnPanePropertyChanged;
        if (Session is { } session)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
        }
        RemoteFiles.Detach();
        // Dispose 而不只是 Detach:本地面板还挂着一个 FileSystemWatcher(目录自动刷新),
        // 不释放就是一个跟着文档一起泄漏的系统句柄。
        LocalFiles.Dispose();
    }

    /// <summary>关闭 SFTP 会话并清理资源,确保仅执行一次。</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_closeSync)
        {
            _closeTask ??= CloseCoreAsync();
            return cancellationToken.CanBeCanceled
                ? _closeTask.WaitAsync(cancellationToken)
                : _closeTask;
        }
    }

    /// <summary>异步释放 SFTP 文档视图模型。</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        await LocalFiles.LoadInitialAsync(_lifetime.Token);
        await RemoteFiles.LoadInitialAsync(_lifetime.Token);
    }

    private async Task CloseCoreAsync()
    {
        Detach();

        // 本会话名下还开着的远程编辑会话先收尾:把没传完的传掉,再停止监视。
        // 顺序要紧 —— 得赶在 SFTP 连接被关掉之前,否则那几次补传注定失败,
        // 用户只会看到一串"上传失败",而改动其实本来传得上去。
        await Services.RemoteEditSessionManager.CloseScopeAsync(SessionId).ConfigureAwait(false);
        try
        {
            await _serializedSftp.CloseAsync().ConfigureAwait(false);

            // 等初始加载收尾,但**不能**把它的取消当成关闭失败上报:上面的 Detach() 刚刚
            // 取消了 _lifetime,若初始加载还在飞(开标签后立刻关就会这样),它必然以
            // OperationCanceledException 收场 —— 那是本方法自己造成的,不是错误。
            // 其它异常照常抛出。
            //
            // 实测栈(2026-07-22,SftpDocumentClose_WhenCallerWaitIsCancelled_... 偶发红):
            //   LocalFilePaneViewModel.RefreshRootsAsync → LoadInitialAsync → LoadAsync
            //   → CloseCoreAsync 原样抛出 TaskCanceledException。
            // 注意本地栏与远程栏在这点上不一致:远程栏内部吞掉了自身的取消,本地栏没有,
            // 所以只有本地栏还在飞时才会炸 —— 这也是它只在混跑时偶发的原因。
            // 无对应单测:要稳定复现须把 ILocalRootProvider 从 SftpDocumentViewModel 一路穿透
            // 注入进 LocalFilePaneViewModel(目前只有后者的双参构造有这个注入点),
            // 属于为可测性改生产 API,暂未做。
            try
            {
                await InitialLoadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await _disconnectAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
