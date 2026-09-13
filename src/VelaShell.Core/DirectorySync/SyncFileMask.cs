namespace VelaShell.Core.DirectorySync;

/// <summary>
/// 同步用的文件掩码,写法照搬 WinSCP:<c>包含1; 包含2 | 排除1; 排除2</c>。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>以 <c>;</c> 分隔多个掩码,以一个 <c>|</c> 分开「包含」与「排除」两段;两段都可以省略。</item>
///   <item>以 <c>/</c> 结尾的是<b>目录掩码</b>,只作用于目录;其余只作用于文件。</item>
///   <item>中间含 <c>/</c> 的按相对路径匹配(<c>src/obj/</c>),否则只按名字匹配(<c>*.log</c>)。</item>
///   <item>通配符 <c>*</c>(任意串)与 <c>?</c>(单个字符),不区分大小写;<c>*.*</c> 等同 <c>*</c>。</item>
///   <item>没有文件包含掩码时所有文件都算包含,目录同理 —— 所以 <c>*.php</c> 不会把目录挡在外面。</item>
/// </list>
/// 被排除的目录<b>不会被扫描</b>(<c>| node_modules/</c> 省下的正是那几万次列举),
/// 也就不会出现在比较结果里,删除多余文件时自然不会删到它。
/// </remarks>
public sealed class SyncFileMask
{
    private readonly Pattern[] _includeFiles;
    private readonly Pattern[] _includeDirectories;
    private readonly Pattern[] _excludes;

    private SyncFileMask(string source, Pattern[] includes, Pattern[] excludes)
    {
        Source = source;
        _includeFiles = [.. includes.Where(static p => !p.IsDirectory)];
        _includeDirectories = [.. includes.Where(static p => p.IsDirectory)];
        _excludes = excludes;
    }

    /// <summary>空掩码:什么都包含。</summary>
    public static SyncFileMask Empty { get; } = new(string.Empty, [], []);

    /// <summary>原始文本。</summary>
    public string Source { get; }

    /// <summary>是否为空掩码。</summary>
    public bool IsEmpty => _includeFiles.Length == 0 && _includeDirectories.Length == 0 && _excludes.Length == 0;

    /// <summary>解析掩码;格式不对时返回 false 并给出原因(英文,供日志;界面另有本地化文案)。</summary>
    public static bool TryParse(string? text, out SyncFileMask mask, out string? error)
    {
        mask = Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        string[] halves = text.Split('|');
        if (halves.Length > 2)
        {
            error = "A mask may contain only one '|'.";
            return false;
        }
        if (!TryParsePatterns(halves[0], out Pattern[] includes, out error)
            || !TryParsePatterns(halves.Length > 1 ? halves[1] : string.Empty, out Pattern[] excludes, out error))
        {
            return false;
        }
        mask = new(text.Trim(), includes, excludes);
        return true;
    }

    /// <summary>
    /// 该相对路径是否纳入同步。排除优先于包含;包含掩码只约束同类(文件掩码管文件,目录掩码管目录)。
    /// </summary>
    /// <param name="relativePath">相对于同步根、以 <c>/</c> 分隔的路径。</param>
    /// <param name="isDirectory">是否目录。</param>
    public bool Includes(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        foreach (Pattern exclude in _excludes)
        {
            if (exclude.IsDirectory == isDirectory && exclude.Matches(relativePath, name))
            {
                return false;
            }
        }
        Pattern[] includes = isDirectory ? _includeDirectories : _includeFiles;
        if (includes.Length == 0)
        {
            return true;
        }
        foreach (Pattern include in includes)
        {
            if (include.Matches(relativePath, name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParsePatterns(string segment, out Pattern[] patterns, out string? error)
    {
        var list = new List<Pattern>();
        error = null;
        foreach (string raw in segment.Split(';'))
        {
            string token = raw.Trim().Replace('\\', '/');
            if (token.Length == 0)
            {
                continue;
            }
            bool isDirectory = token.EndsWith('/');
            string body = token.TrimEnd('/');
            bool isPath = body.Contains('/');
            body = body.TrimStart('/');
            if (body.Length == 0)
            {
                error = $"'{raw.Trim()}' is not a valid mask.";
                patterns = [];
                return false;
            }
            list.Add(new(body == "*.*" ? "*" : body, isDirectory, isPath));
        }
        patterns = [.. list];
        return true;
    }

    /// <summary>单个掩码。</summary>
    private sealed record Pattern(string Body, bool IsDirectory, bool IsPath)
    {
        public bool Matches(string relativePath, string name) => Wildcard(Body, IsPath ? relativePath : name);
    }

    /// <summary>
    /// <c>*</c> / <c>?</c> 通配,不区分大小写。双指针 + 回溯到最近一个 <c>*</c>,线性时间,
    /// 不走正则 —— 扫一个大目录树时每个条目都要过一遍,也免得掩码里的 <c>.</c>、<c>[</c> 被当成正则元字符。
    /// </summary>
    public static bool Wildcard(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int star = -1;
        int mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
