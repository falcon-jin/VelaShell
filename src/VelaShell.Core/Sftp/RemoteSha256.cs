namespace VelaShell.Core.Sftp;

/// <summary>
/// 经 SSH exec 通道在远端批量算 SHA-256:拼命令、解析输出。与连接无关的纯函数,单独拎出来才测得到引号与转义。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>整段脚本交给 <c>sh -c</c>:用户的登录 shell 可能是 fish / csh,直接跑 <c>if … fi</c> 会语法错;
///   <c>sh -c '脚本' 名字 参数…</c> 这一行本身在这几种 shell 里都解析得通。</item>
///   <item>工具按 <c>sha256sum</c>(GNU / busybox)→ <c>shasum -a 256</c>(macOS / BSD)的顺序找,都没有就打印
///   <see cref="UnavailableMarker" />,由 <see cref="Parse" /> 翻译成 <see cref="NotSupportedException" />。</item>
///   <item>路径逐个单引号包起来(内嵌的 <c>'</c> 写成 <c>'\''</c>),脚本里用 <c>"$@"</c> 原样传下去,不经任何二次展开。</item>
/// </list>
/// </remarks>
public static class RemoteSha256
{
    /// <summary>远端两个工具都没有时脚本打印的标记。</summary>
    public const string UnavailableMarker = "VELA-NO-SHA256";

    /// <summary>脚本本体。里面不能出现单引号 —— 它整段被单引号包着交给 <c>sh -c</c>。</summary>
    private const string Script =
        "if command -v sha256sum >/dev/null 2>&1; then exec sha256sum -- \"$@\"; " +
        "elif command -v shasum >/dev/null 2>&1; then exec shasum -a 256 -- \"$@\"; " +
        "else echo " + UnavailableMarker + "; fi";

    /// <summary>拼出对这批路径算 SHA-256 的命令行。</summary>
    public static string BuildCommand(IReadOnlyList<string> remotePaths)
    {
        ArgumentNullException.ThrowIfNull(remotePaths);
        return "sh -c '" + Script + "' vela-sha256 " + string.Join(' ', remotePaths.Select(Quote));
    }

    /// <summary>POSIX shell 的单引号转义。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// 解析 <c>sha256sum</c> / <c>shasum</c> 的输出,返回 请求的路径 → 小写十六进制摘要;某个文件没算出来(读不了、
    /// 刚被删)时对应值为 null。整个批次都算不了(没有工具、不是 POSIX 主机、输出认不出)时抛 <see cref="NotSupportedException" />。
    /// </summary>
    /// <param name="standardOutput">标准输出。</param>
    /// <param name="standardError">标准错误。</param>
    /// <param name="exitCode">退出码。<c>sha256sum</c> 有文件失败时退出 1,但其余文件照常输出。</param>
    /// <param name="requested">请求时传入的路径(输出里的文件名与它逐字一致)。</param>
    public static IReadOnlyDictionary<string, string?> Parse(
        string standardOutput,
        string standardError,
        int exitCode,
        IReadOnlyList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string path in requested)
        {
            result[path] = null;
        }
        int parsed = 0;
        foreach (string raw in (standardOutput ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim() == UnavailableMarker)
            {
                throw new NotSupportedException("Neither sha256sum nor shasum is available on the remote host.");
            }
            // GNU 格式:「摘要␠␠名字」或「摘要␠*名字」;名字里有反斜杠或换行时整行以 \ 开头,名字里的 \\ \n \r 被转义。
            bool escaped = line.StartsWith('\\');
            string body = escaped ? line[1..] : line;
            if (body.Length < 67 || !IsHex(body.AsSpan(0, 64)) || body[64] != ' ' || body[65] is not (' ' or '*'))
            {
                continue;
            }
            parsed++;
            string name = escaped ? Unescape(body[66..]) : body[66..];
            if (result.ContainsKey(name))
            {
                result[name] = body[..64].ToLowerInvariant();
            }
        }
        if (parsed > 0 || requested.Count == 0)
        {
            return result;
        }

        // 一行都没认出来:如果标准错误里全是工具自己报的逐文件错误(退出码 1),那只是这批文件恰好都读不了;
        // 否则就是这台主机根本跑不了这条命令(命令不存在、Windows 的 cmd、exec 被 ForceCommand 禁掉)。
        string[] errors = (standardError ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool onlyPerFileErrors = exitCode == 1
                                 && errors.Length > 0
                                 && errors.All(static e => e.StartsWith("sha256sum:", StringComparison.Ordinal)
                                                           || e.StartsWith("shasum:", StringComparison.Ordinal));
        if (onlyPerFileErrors)
        {
            return result;
        }
        string reason = errors.FirstOrDefault() ?? $"exit code {exitCode}, no recognizable output";
        throw new NotSupportedException($"Remote SHA-256 is unavailable: {reason}");
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static string Unescape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    _ => next,
                });
            }
            else
            {
                builder.Append(value[i]);
            }
        }
        return builder.ToString();
    }
}
