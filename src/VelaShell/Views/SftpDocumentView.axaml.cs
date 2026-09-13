using Avalonia.Controls;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>独立的 SFTP 文档视图,采用双栏布局。</summary>
public partial class SftpDocumentView : UserControl
{
    /// <summary>初始化 SFTP 文档视图。</summary>
    public SftpDocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SftpDocumentViewModel vm)
            {
                vm.ShowSyncWindow = ShowSyncWindowAsync;
            }
        };
    }

    /// <summary>
    /// 显示同步窗口;这个视图模型的窗口已经开着就把它提到前面。窗口按视图模型找而不是记在视图字段里:
    /// 切标签时文档视图会被重建,字段跟着丢,而窗口与视图模型都还活着。
    /// </summary>
    private Task ShowSyncWindowAsync(DirectorySyncViewModel sync)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        if (owner.OwnedWindows.OfType<DirectorySyncWindow>().FirstOrDefault(w => ReferenceEquals(w.DataContext, sync)) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return Task.CompletedTask;
        }
        new DirectorySyncWindow { DataContext = sync }.Show(owner);
        return Task.CompletedTask;
    }
}
