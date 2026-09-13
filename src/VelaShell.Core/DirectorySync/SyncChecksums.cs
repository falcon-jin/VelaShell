using System.Collections.Concurrent;
using System.Security.Cryptography;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.DirectorySync;

/// <summary>校验阶段的进度:已处理 / 总数(远端与本地各算一遍,所以总数是候选文件对数的两倍)。</summary>
/// <param name="Done">已处理。</param>
/// <param name="Total">总数。</param>
public readonly record struct SyncChecksumProgress(int Done, int Total);

/// <summary>校验阶段的结果。</summary>
/// <param name="Local">本地条目(两边都算出摘要的文件带上了 <see cref="SyncItem.Sha256" />)。</param>
/// <param name="Remote">远端条目(同上)。</param>
/// <param name="Candidates">需要校验的文件对数:两边都有、大小相同。</param>
/// <param name="Verified">两边都拿到了摘要的文件对数。</param>
/// <param name="RemoteFailure">远端整体算不了时的原因(不支持或出错);此时全部回退到大小与修改时间。</param>
public sealed record SyncChecksumOutcome(
    IReadOnlyList<SyncItem> Local,
    IReadOnlyList<SyncItem> Remote,
    int Candidates,
    int Verified,
    string? RemoteFailure)
{
    /// <summary>回退到大小与修改时间比较的文件对数。</summary>
    public int FellBack => Candidates - Verified;
}

/// <summary>
/// 摘要缓存,以「哪一边 + 完整路径 + 大小 + 修改时间」为键。同步完的自动复查、保持最新的每一轮都会再比较一次,
/// 没变过的文件不该每次都整份读一遍;大小或时间一变,键就对不上,自然重算。
/// </summary>
/// <remarks>
/// 只挂在同步窗口 / 双栏文档上,关了就丢:「内容改了但大小与时间都没变」只有刻意 <c>touch -r</c> 才造得出来,
/// 缓存活得越短,这种情况被缓存盖住的机会越小。
/// </remarks>
public sealed class SyncChecksumCache
{
    private readonly ConcurrentDictionary<(bool Remote, string Path, long Size, long Ticks), string> _entries = new();

    /// <summary>取缓存。</summary>
    public bool TryGet(bool remote, SyncItem item, out string hash) =>
        _entries.TryGetValue(Key(remote, item), out hash!);

    /// <summary>写缓存。</summary>
    public void Set(bool remote, SyncItem item, string hash) => _entries[Key(remote, item)] = hash;

    private static (bool, string, long, long) Key(bool remote, SyncItem item) =>
        (remote, item.FullPath, item.Size, item.LastWriteTimeUtc.Ticks);
}

/// <summary>
/// 比较前的 SHA-256 阶段:给「两边都有、大小相同」的文件对算摘要,算得出来的按内容比,算不出来的回退到大小与修改时间。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>只算大小相同的</b>:大小不同已经证明内容不同,读两边整份文件只为再证明一次是纯浪费。</item>
///   <item><b>先远端、后本地</b>:服务器不支持时本地一个字节都不读。</item>
///   <item><b>回退分两级</b>:远端整体不支持或出错 → 余下的全部回退(不再一批批撞同一堵墙);
///   单个文件读不了 → 只有它回退。</item>
/// </list>
/// </remarks>
public static class SyncChecksums
{
    /// <summary>一次 exec 最多带多少个路径。</summary>
    public const int MaxBatchFiles = 200;

    /// <summary>一次 exec 的路径总长上限:远离各家 sshd / shell 的命令行长度限制。</summary>
    internal const int MaxBatchChars = 16_000;

    /// <summary>执行校验阶段;选项里没勾校验时原样返回。</summary>
    public static async Task<SyncChecksumOutcome> ApplyAsync(
        IReadOnlyList<SyncItem> local,
        IReadOnlyList<SyncItem> remote,
        SyncOptions options,
        ISftpService sftp,
        Guid sessionId,
        SyncChecksumCache? cache = null,
        IProgress<SyncChecksumProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sftp);
        if (!options.Criteria.HasFlag(SyncCriteria.Checksum))
        {
            return new(local, remote, 0, 0, null);
        }

        var remoteByPath = new Dictionary<string, SyncItem>(DirectoryComparer.PathComparer(options));
        foreach (SyncItem item in remote.Where(static r => !r.IsDirectory))
        {
            remoteByPath.TryAdd(item.RelativePath, item);
        }
        var pairs = new List<(SyncItem Local, SyncItem Remote)>();
        foreach (SyncItem item in local.Where(static l => !l.IsDirectory))
        {
            if (remoteByPath.TryGetValue(item.RelativePath, out SyncItem? other) && other.Size == item.Size)
            {
                pairs.Add((item, other));
            }
        }
        if (pairs.Count == 0)
        {
            return new(local, remote, 0, 0, null);
        }

        int total = pairs.Count * 2;
        int done = 0;
        void Report() => progress?.Report(new(Volatile.Read(ref done), total));

        // —— 远端 ——
        var remoteHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<SyncItem>();
        foreach ((_, SyncItem item) in pairs)
        {
            if (cache is not null && cache.TryGet(true, item, out string cached))
            {
                remoteHashes[item.FullPath] = cached;
                done++;
            }
            else
            {
                pending.Add(item);
            }
        }
        Report();
        string? failure = null;
        foreach (List<SyncItem> batch in Batches(pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyDictionary<string, string?> hashes = await sftp
                    .ComputeSha256Async(sessionId, [.. batch.Select(static i => i.FullPath)], cancellationToken)
                    .ConfigureAwait(false);
                foreach (SyncItem item in batch)
                {
                    if (hashes.TryGetValue(item.FullPath, out string? hash) && hash is { Length: 64 })
                    {
                        remoteHashes[item.FullPath] = hash;
                        cache?.Set(true, item, hash);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 不支持(NotSupportedException)与出错一样处理:余下的整体回退,不再一批批撞同一堵墙。
                failure = ex.Message;
                break;
            }
            done += batch.Count;
            Report();
        }

        // —— 本地:只算远端拿到了摘要的那些 ——
        (SyncItem Local, SyncItem Remote)[] hashable = [.. pairs.Where(p => remoteHashes.ContainsKey(p.Remote.FullPath))];
        Volatile.Write(ref done, pairs.Count + (pairs.Count - hashable.Length));
        Report();
        var localHashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            hashable,
            new ParallelOptions
            {
                // 读盘为主:并发开太多只会让机械盘来回寻道。
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = cancellationToken,
            },
            async (pair, token) =>
            {
                SyncItem item = pair.Local;
                if (cache is not null && cache.TryGet(false, item, out string cached))
                {
                    localHashes[item.FullPath] = cached;
                }
                else
                {
                    try
                    {
                        string hash = await HashLocalFileAsync(item.FullPath, token).ConfigureAwait(false);
                        localHashes[item.FullPath] = hash;
                        cache?.Set(false, item, hash);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 这一个文件回退到大小与修改时间。
                    }
                }
                Interlocked.Increment(ref done);
                Report();
            }).ConfigureAwait(false);

        // —— 回填 ——
        var localSet = new Dictionary<SyncItem, string>(ReferenceEqualityComparer.Instance);
        var remoteSet = new Dictionary<SyncItem, string>(ReferenceEqualityComparer.Instance);
        foreach ((SyncItem l, SyncItem r) in hashable)
        {
            if (localHashes.TryGetValue(l.FullPath, out string? lh) && remoteHashes.TryGetValue(r.FullPath, out string? rh))
            {
                localSet[l] = lh;
                remoteSet[r] = rh;
            }
        }
        return new(
            [.. local.Select(i => localSet.TryGetValue(i, out string? h) ? i with { Sha256 = h } : i)],
            [.. remote.Select(i => remoteSet.TryGetValue(i, out string? h) ? i with { Sha256 = h } : i)],
            pairs.Count,
            localSet.Count,
            failure);
    }

    /// <summary>算本地文件的 SHA-256(小写十六进制)。共享读,不挡住正在写它的程序。</summary>
    public static async Task<string> HashLocalFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>按文件数与路径总长切批。</summary>
    public static IEnumerable<List<SyncItem>> Batches(IEnumerable<SyncItem> items)
    {
        var batch = new List<SyncItem>();
        int chars = 0;
        foreach (SyncItem item in items)
        {
            int cost = item.FullPath.Length + 3;
            if (batch.Count > 0 && (batch.Count >= MaxBatchFiles || chars + cost > MaxBatchChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }
            batch.Add(item);
            chars += cost;
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
