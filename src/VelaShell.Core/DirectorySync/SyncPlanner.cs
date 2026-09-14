namespace VelaShell.Core.DirectorySync;

/// <summary>
/// 把比较结论按方向与模式翻译成要执行的步骤。纯逻辑:同一份比较结果、同一组选项,永远得到同一份计划。
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>结论</term><description>单向(源 → 目标)</description></listheader>
///   <item><term>只有源有</term><description>传过去(目录则新建);勾了「仅已存在的文件」时跳过</description></item>
///   <item><term>只有目标有</term><description>勾了「删除多余文件」才删;删目录时它下面的条目不再单列</description></item>
///   <item><term>源较新 / 内容不同</term><description>传过去</description></item>
///   <item><term>目标较新</term><description>同步模式不动,镜像模式照样覆盖</description></item>
///   <item><term>冲突 / 相同</term><description>不动</description></item>
/// </list>
/// 双向时:只有一边有的补到另一边,较新的覆盖较旧的;时间相同而大小不同的算冲突,不动;从不删除。
/// </remarks>
public static class SyncPlanner
{
    /// <summary>生成同步计划,顺序与比较结果一致(按路径)。</summary>
    public static IReadOnlyList<SyncAction> Plan(IReadOnlyList<SyncComparison> comparisons, SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        ArgumentNullException.ThrowIfNull(options);
        var actions = new List<SyncAction>();
        var deletedDirectories = new HashSet<string>(DirectoryComparer.PathComparer(options));
        foreach (SyncComparison comparison in comparisons)
        {
            if (DirectoryComparer.HasAncestorIn(comparison.RelativePath, deletedDirectories))
            {
                continue;
            }
            SyncAction? action = options.Direction switch
            {
                SyncDirection.Both => PlanBoth(comparison, options),
                SyncDirection.ToRemote => PlanOneWay(comparison, options, toRemote: true),
                _ => PlanOneWay(comparison, options, toRemote: false),
            };
            if (action is null)
            {
                continue;
            }
            if (action.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote && action.IsDirectory)
            {
                deletedDirectories.Add(comparison.RelativePath);
            }
            actions.Add(action);
        }
        return actions;
    }

    private static SyncAction? PlanOneWay(SyncComparison c, SyncOptions options, bool toRemote)
    {
        SyncItem? source = toRemote ? c.Local : c.Remote;
        SyncItem? target = toRemote ? c.Remote : c.Local;

        if (options.EffectiveMode == SyncMode.Timestamps)
        {
            // 时间戳模式与所选依据无关:它本来就是「只比时间」。大小不同、或算出了摘要而摘要不同的不改 ——
            // 那说明内容真的不同,把时间对齐只会让下一次同步再也看不出这处差异。
            if (c.State == SyncComparisonState.Conflict
                || source is not { IsDirectory: false }
                || target is not { IsDirectory: false }
                || source.Size != target.Size
                || (source.Sha256 is { } a && target.Sha256 is { } b && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                || SyncTime.Compare(source, target, options.TimeTolerance) == 0)
            {
                return null;
            }
            return Make(c, toRemote ? SyncActionKind.SetRemoteTime : SyncActionKind.SetLocalTime);
        }

        SyncComparisonState sourceOnly = toRemote ? SyncComparisonState.LocalOnly : SyncComparisonState.RemoteOnly;
        SyncComparisonState targetOnly = toRemote ? SyncComparisonState.RemoteOnly : SyncComparisonState.LocalOnly;
        SyncComparisonState sourceNewer = toRemote ? SyncComparisonState.LocalNewer : SyncComparisonState.RemoteNewer;
        SyncComparisonState targetNewer = toRemote ? SyncComparisonState.RemoteNewer : SyncComparisonState.LocalNewer;

        if (c.State == sourceOnly)
        {
            if (options.ExistingOnly)
            {
                return null;
            }
            return c.IsDirectory
                ? Make(c, toRemote ? SyncActionKind.CreateRemoteDirectory : SyncActionKind.CreateLocalDirectory)
                : Make(c, toRemote ? SyncActionKind.Upload : SyncActionKind.Download);
        }
        if (c.State == targetOnly)
        {
            return options.EffectiveDeleteExtraneous
                ? Make(c, toRemote ? SyncActionKind.DeleteRemote : SyncActionKind.DeleteLocal)
                : null;
        }
        if (c.State == sourceNewer
            || c.State == SyncComparisonState.Differs
            || (c.State == targetNewer && options.EffectiveMode == SyncMode.Mirror))
        {
            return Make(c, toRemote ? SyncActionKind.Upload : SyncActionKind.Download);
        }
        return null;
    }

    private static SyncAction? PlanBoth(SyncComparison c, SyncOptions options) => c.State switch
    {
        SyncComparisonState.LocalOnly when !options.ExistingOnly =>
            Make(c, c.IsDirectory ? SyncActionKind.CreateRemoteDirectory : SyncActionKind.Upload),
        SyncComparisonState.RemoteOnly when !options.ExistingOnly =>
            Make(c, c.IsDirectory ? SyncActionKind.CreateLocalDirectory : SyncActionKind.Download),
        SyncComparisonState.LocalNewer => Make(c, SyncActionKind.Upload),
        SyncComparisonState.RemoteNewer => Make(c, SyncActionKind.Download),
        // 时间一样、大小不一样:双向时没有「源」,谁覆盖谁都可能毁掉一份改动。
        _ => null,
    };

    private static SyncAction Make(SyncComparison c, SyncActionKind kind) =>
        new(kind, c.RelativePath, c.IsDirectory, c.Local, c.Remote);
}
