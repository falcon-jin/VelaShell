using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace VelaShell.Core.FileTransfer.Diagnostics;

/// <summary>
/// 终端内文件传输(ZMODEM / XMODEM / YMODEM)的逐帧诊断日志。默认关闭且零开销(不写一行、
/// 不建文件);把环境变量 <c>VELASHELL_TRANSFER_TRACE</c>(或历史名 <c>VELASHELL_ZMODEM_TRACE</c>)
/// 设为 <c>1</c> 即启用,日志写到 <c>%TEMP%/velashell-transfer.log</c>
/// (可用 <c>VELASHELL_TRANSFER_TRACE_PATH</c> / <c>VELASHELL_ZMODEM_TRACE_PATH</c> 覆盖)。
/// 每次进程启动重建文件(不跨运行累积),单次运行写满 <see cref="MaxLogBytes" /> 后自动停笔 ——
/// 排障要的是头部的握手帧,不是几百 MB 数据分片。
/// 传输卡住时终端本身什么都看不到(字节全被路由器接管),没有这个日志就只能靠猜 ——
/// 这正是 2026-07 CRC 双重增广 bug 排查受阻的原因(见 Crc16Xmodem 注释)。
/// </summary>
public static class TransferTrace
{
    /// <summary>单次运行的日志体积上限;超过后停止记录,只补一行截断标记。</summary>
    private const long MaxLogBytes = 20 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string? Path = Initialize();

    private static long _written;
    private static bool _capped;

    /// <summary>是否已启用诊断日志。</summary>
    public static bool IsEnabled => Path is not null;

    private static string? Initialize()
    {
        // 新旧两个变量名都认:历史文档与既有排障习惯(VELASHELL_ZMODEM_TRACE)不能因改名失效。
        if (!IsTruthy(Environment.GetEnvironmentVariable("VELASHELL_TRANSFER_TRACE")) &&
            !IsTruthy(Environment.GetEnvironmentVariable("VELASHELL_ZMODEM_TRACE")))
        {
            return null;
        }
        string? custom = Environment.GetEnvironmentVariable("VELASHELL_TRANSFER_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(custom))
        {
            custom = Environment.GetEnvironmentVariable("VELASHELL_ZMODEM_TRACE_PATH");
        }
        string path = string.IsNullOrWhiteSpace(custom)
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velashell-transfer.log")
            : custom;
        try
        {
            // 每次启动重建:旧运行的日志已经排完障就没用了,留着只会无限膨胀。
            File.WriteAllText(
                path,
                $"# VelaShell transfer trace — process started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}");
        }
        catch
        {
            return null;
        }
        return path;
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 记录一行诊断信息(插值字符串版)。日志关闭时连字符串都不拼、插值里的表达式也不求值 ——
    /// 这些调用挂在逐帧 / 逐子包的路径上,默认关闭的日志不该让每个数据块都多一次字符串分配。
    /// </summary>
    /// <param name="message">由编译器经 <see cref="MessageHandler" /> 构造的插值消息。</param>
    public static void Log(ref MessageHandler message)
    {
        if (Path is null)
        {
            return;
        }
        Log(message.ToStringAndClear());
    }

    /// <summary>
    /// <see cref="Log(ref MessageHandler)" /> 的插值处理器:构造时就问一次日志是否开启,
    /// 关闭时经 <c>out bool</c> 告诉编译器跳过全部拼接与求值。
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct MessageHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        /// <summary>由编译器调用。</summary>
        /// <param name="literalLength">字面部分总长。</param>
        /// <param name="formattedCount">插值洞数量。</param>
        /// <param name="enabled">日志是否开启;为 false 时编译器不再调用任何 Append。</param>
        public MessageHandler(int literalLength, int formattedCount, out bool enabled)
        {
            enabled = IsEnabled;
            _inner = enabled
                ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture)
                : default;
        }

        /// <summary>由编译器调用。</summary>
        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        /// <summary>由编译器调用。</summary>
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        /// <summary>由编译器调用。</summary>
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

        /// <summary>由编译器调用。</summary>
        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

        internal string ToStringAndClear() => _inner.ToStringAndClear();
    }

    /// <summary>记录一行诊断信息(带毫秒时间戳与线程号)。</summary>
    /// <param name="message">要记录的信息。</param>
    public static void Log(string message)
    {
        if (Path is null)
        {
            return;
        }
        try
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0,8:F1}ms t{1,-3}] {2}{3}",
                Clock.Elapsed.TotalMilliseconds,
                Environment.CurrentManagedThreadId,
                message,
                Environment.NewLine);
            lock (Gate)
            {
                if (_capped)
                {
                    return;
                }
                if (_written >= MaxLogBytes)
                {
                    _capped = true;
                    File.AppendAllText(Path, $"# trace stopped: size cap ({MaxLogBytes / (1024 * 1024)}MB) reached{Environment.NewLine}");
                    return;
                }
                File.AppendAllText(Path, line);
                _written += line.Length;
            }
        }
        catch
        {
            // 诊断日志永远不能影响传输本身。
        }
    }

    /// <summary>记录一段链路字节(十六进制 + 可打印字符),超长自动截断。</summary>
    /// <param name="direction">方向标记,如 <c>"TX"</c> / <c>"RX"</c>。</param>
    /// <param name="data">链路字节。</param>
    /// <param name="max">最多记录多少字节。</param>
    public static void LogBytes(string direction, ReadOnlySpan<byte> data, int max = 64)
    {
        if (Path is null)
        {
            return;
        }
        int take = Math.Min(max, data.Length);
        var hex = new StringBuilder(take * 3);
        var ascii = new StringBuilder(take);
        for (int i = 0; i < take; i++)
        {
            hex.Append(data[i].ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
            ascii.Append(data[i] is >= 0x20 and < 0x7F ? (char)data[i] : '.');
        }
        string suffix = data.Length > take ? $" …(+{data.Length - take}B)" : string.Empty;
        Log($"{direction} {data.Length,5}B  {hex}|{ascii}|{suffix}");
    }
}
