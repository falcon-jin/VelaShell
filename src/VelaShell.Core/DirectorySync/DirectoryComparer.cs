namespace VelaShell.Core.DirectorySync;

/// <summary>
/// 把两棵扫描结果按相对路径配对,逐对给出结论。纯逻辑,不碰任何 IO。
/// </summary>
public static class DirectoryComparer
{
    /// <summary>比较本地与远端两组条目,结果按路径排序(父目录总在子项之前)。</summary>
    /// <param name="local">本地条目。</param>
    /// <param name="remote">远端条目。</param>
    /// <param name="options">比较依据、大小写与时间容差。</param>
    public static IReadOnlyList<SyncComparison> Compare(IEnumerable<SyncItem> local, IEnumerable<SyncItem> remote, SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        StringComparer comparer = PathComparer(options);
        var localMap = new Dictionary<string, SyncItem>(comparer);
        var remoteMap = new Dictionary<string, SyncItem>(comparer);
        var collisions = new HashSet<string>(comparer);
        foreach (SyncItem item in local)
        {
            if (!localMap.TryAdd(item.RelativePath, item))
            {
                collisions.Add(item.RelativePath);
            }
        }
        foreach (SyncItem item in remote)
        {
            // 远端 Linux 上 README.md 与 readme.md 可以并存,本地 Windows 上只能留一个:
            // 谁覆盖谁都是在替用户做决定,于是整对标成冲突,不动它。
            if (!remoteMap.TryAdd(item.RelativePath, item))
            {
                collisions.Add(item.RelativePath);
            }
        }

        var paths = new SortedSet<string>(localMap.Keys.Concat(remoteMap.Keys), StringComparer.Ordinal);
        var blocked = new HashSet<string>(comparer);
        var results = new List<SyncComparison>(paths.Count);
        var seen = new HashSet<string>(comparer);
        foreach (string path in paths)
        {
            // 忽略大小写时同一个键会以两种拼写各出现一次(本地 a.txt、远端 A.txt),只取一次。
            if (!seen.Add(path) || HasAncestorIn(path, blocked))
            {
                continue;
            }
            localMap.TryGetValue(path, out SyncItem? l);
            remoteMap.TryGetValue(path, out SyncItem? r);
            SyncComparisonState state;
            if (collisions.Contains(path) || (l is not null && r is not null && l.IsDirectory != r.IsDirectory))
            {
                state = SyncComparisonState.Conflict;
                // 冲突的目录下面不再往下比:一边是文件、一边是目录时,目录里的子项全会变成
                // 「只有一边有」,勾上删除就会去删一棵用户根本没打算动的树。
                if ((l?.IsDirectory ?? false) || (r?.IsDirectory ?? false))
                {
                    blocked.Add(path);
                }
            }
            else if (r is null)
            {
                state = SyncComparisonState.LocalOnly;
            }
            else if (l is null)
            {
                state = SyncComparisonState.RemoteOnly;
            }
            else if (l.IsDirectory)
            {
                state = SyncComparisonState.Same;
            }
            else
            {
                state = CompareFiles(l, r, options);
            }
            results.Add(new(l?.RelativePath ?? r!.RelativePath, l, r, state));
        }
        return results;
    }

    /// <summary>
    /// 两个同名文件按所选依据的结论。两边都有 SHA-256 时内容说了算;否则时间优先,时间相同(或不比时间)再看大小。
    /// </summary>
    public static SyncComparisonState CompareFiles(SyncItem local, SyncItem remote, SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        bool checksum = options.Criteria.HasFlag(SyncCriteria.Checksum);
        if (checksum && local.Sha256 is { } localHash && remote.Sha256 is { } remoteHash)
        {
            // 摘要相同就是同一个文件,哪怕时间不同(git checkout、解压、touch 都会改时间)也不传;
            // 摘要不同时内容确实变了,只能靠时间判断哪边较新。
            if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                return SyncComparisonState.Same;
            }
            int newer = options.Criteria.HasFlag(SyncCriteria.Time)
                ? SyncTime.Compare(local, remote, options.TimeTolerance)
                : 0;
            return newer > 0 ? SyncComparisonState.LocalNewer
                : newer < 0 ? SyncComparisonState.RemoteNewer
                : SyncComparisonState.Differs;
        }

        // 回退:按勾选的时间 / 大小比较;只勾了校验而两样都没勾时,回退就按两样都比 —— 回退不能退成「什么都不比」。
        SyncCriteria fallback = options.Criteria & (SyncCriteria.Time | SyncCriteria.Size);
        if (checksum && fallback == SyncCriteria.None)
        {
            fallback = SyncCriteria.Time | SyncCriteria.Size;
        }
        if (fallback.HasFlag(SyncCriteria.Time))
        {
            int time = SyncTime.Compare(local, remote, options.TimeTolerance);
            if (time != 0)
            {
                return time > 0 ? SyncComparisonState.LocalNewer : SyncComparisonState.RemoteNewer;
            }
        }
        return fallback.HasFlag(SyncCriteria.Size) && local.Size != remote.Size
            ? SyncComparisonState.Differs
            : SyncComparisonState.Same;
    }

    /// <summary>按选项取路径比较器。</summary>
    internal static StringComparer PathComparer(SyncOptions options) =>
        options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><paramref name="path" /> 的某个上级目录是否在集合里(不含自身)。</summary>
    internal static bool HasAncestorIn(string path, HashSet<string> ancestors)
    {
        if (ancestors.Count == 0)
        {
            return false;
        }
        for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (ancestors.Contains(path[..slash]))
            {
                return true;
            }
        }
        return false;
    }
}
