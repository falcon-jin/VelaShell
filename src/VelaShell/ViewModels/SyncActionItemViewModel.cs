using System.Globalization;
using ReactiveUI;
using VelaShell.Core.DirectorySync;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>同步预览里的一行:一步操作,可以取消勾选。</summary>
/// <param name="action">这一行对应的步骤。</param>
public sealed class SyncActionItemViewModel(SyncAction action) : ReactiveObject
{
    /// <summary>对应的步骤。</summary>
    public SyncAction Action { get; } = action ?? throw new ArgumentNullException(nameof(action));

    /// <summary>是否执行这一步。默认勾选:预览本身就是确认,用户只需要把不想要的去掉。</summary>
    public bool IsChecked
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>显示用的相对路径,目录带尾随 <c>/</c>。</summary>
    public string Path => Action.IsDirectory ? Action.RelativePath + "/" : Action.RelativePath;

    /// <summary>动作文案。</summary>
    public string KindText => DescribeKind(Action.Kind);

    /// <summary>上传类(上传、建远端目录)—— 向右的箭头。</summary>
    public bool IsToRemote => Action.Kind is SyncActionKind.Upload or SyncActionKind.CreateRemoteDirectory;

    /// <summary>下载类(下载、建本地目录)。</summary>
    public bool IsToLocal => Action.Kind is SyncActionKind.Download or SyncActionKind.CreateLocalDirectory;

    /// <summary>删除类:以危险色画出来。</summary>
    public bool IsDelete => Action.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote;

    /// <summary>只改时间。</summary>
    public bool IsTimestamp => Action.Kind is SyncActionKind.SetLocalTime or SyncActionKind.SetRemoteTime;

    /// <summary>本地一侧的大小与时间。</summary>
    public string LocalDetail => Describe(Action.Local);

    /// <summary>远端一侧的大小与时间。</summary>
    public string RemoteDetail => Describe(Action.Remote);

    /// <summary>动作类型的本地化文案(预览行与「保持最新」的日志共用)。</summary>
    public static string DescribeKind(SyncActionKind kind) => Strings.Get(kind switch
    {
        SyncActionKind.Upload => "Sync_KindUpload",
        SyncActionKind.Download => "Sync_KindDownload",
        SyncActionKind.CreateRemoteDirectory => "Sync_KindCreateRemoteDirectory",
        SyncActionKind.CreateLocalDirectory => "Sync_KindCreateLocalDirectory",
        SyncActionKind.SetRemoteTime => "Sync_KindSetRemoteTime",
        SyncActionKind.SetLocalTime => "Sync_KindSetLocalTime",
        SyncActionKind.DeleteRemote => "Sync_KindDeleteRemote",
        _ => "Sync_KindDeleteLocal",
    });

    /// <summary>
    /// 「1.2 KB · 2026-09-12 10:30」。时间按该条目的精度显示:FTP 只给到分钟的就不补一个假的 <c>:00</c>,
    /// 用户比两边时间时看到的才是比较器实际用的那一位。
    /// </summary>
    private static string Describe(SyncItem? item)
    {
        if (item is null)
        {
            return "—";
        }
        if (item.IsDirectory)
        {
            return Strings.Folder;
        }
        string size = LocalFileEntry.FormatSize(item.Size);
        string? format = item.Precision switch
        {
            SyncTimePrecision.Second => "yyyy-MM-dd HH:mm:ss",
            SyncTimePrecision.Minute => "yyyy-MM-dd HH:mm",
            SyncTimePrecision.Day => "yyyy-MM-dd",
            _ => null,
        };
        return format is null
            ? size
            : $"{size} · {item.LastWriteTimeUtc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)}";
    }
}
