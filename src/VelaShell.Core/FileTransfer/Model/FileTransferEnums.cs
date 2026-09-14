namespace VelaShell.Core.FileTransfer.Model;

/// <summary>终端内文件传输的方向。</summary>
public enum FileTransferDirection
{
    /// <summary>接收:远端发文件到本地(<c>sz</c> / <c>sb</c> / <c>sx</c>)。</summary>
    Receive,

    /// <summary>发送:本地上传文件到远端(<c>rz</c> / <c>rb</c> / <c>rx</c>)。</summary>
    Send
}

/// <summary>一次会话 / 单个文件的传输状态。</summary>
public enum FileTransferState
{
    /// <summary>尚未开始。</summary>
    Pending,

    /// <summary>正在传输。</summary>
    Transferring,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>被跳过(接收方拒收或文件已存在)。</summary>
    Skipped,

    /// <summary>失败(CRC 反复失败、IO 错误、协议错误)。</summary>
    Failed,

    /// <summary>被取消(用户中止或收到取消序列)。</summary>
    Cancelled
}

/// <summary>接收方对发送方所提供文件的处置决定。</summary>
public enum TransferFileDisposition
{
    /// <summary>从头接收该文件。</summary>
    Accept,

    /// <summary>跳过该文件(ZMODEM 回 ZSKIP;XMODEM/YMODEM 无跳过语义,退化为中止)。</summary>
    Skip,

    /// <summary>中止整个会话。</summary>
    Abort
}

/// <summary>终端内可用的文件传输协议。</summary>
public enum TerminalTransferProtocol
{
    /// <summary>ZMODEM:自动启动、支持批量与断点续传,与 lrzsz <c>sz</c>/<c>rz</c> 互操作。</summary>
    ZModem,

    /// <summary>XMODEM:128 字节块 + CRC16,单文件、无文件名,与 <c>sx</c>/<c>rx</c> 互操作。</summary>
    XModem,

    /// <summary>XMODEM-1K:同 XMODEM,但数据块为 1024 字节(STX 引导)。</summary>
    XModem1K,

    /// <summary>YMODEM(批量):1K 块 + 0 号块携带文件名/大小,与 <c>sb</c>/<c>rb</c> 互操作。</summary>
    YModem,

    /// <summary>YMODEM-G:YMODEM 的流式变体,不逐块应答,依赖无错链路(SSH 天然满足)。</summary>
    YModemG
}

/// <summary>
/// 「发送 / 接收文件」这对通用命令默认走哪条路。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<b>不</b>复用 <see cref="TerminalTransferProtocol" />:那个枚举里的 <c>XModem1K</c> 与
/// <c>YModemG</c> 是引擎参数(块大小、要不要逐块应答),而这里表达的是用户意图 ——
/// 「这台机器上传文件走 SFTP 还是走终端里的 ZMODEM」。粒度不同,而且这里还要多一个
/// <see cref="Sftp" />,它根本不是终端内协议。
/// </para>
/// <para>
/// <see cref="Sftp" /> 只对有 SFTP 通道的连接成立。本地终端、插件终端协议(Telnet / 串口)
/// 没有那条通道,解析时会按启用情况退回终端内协议,见 <c>SessionTransferSettings.Resolve</c>。
/// </para>
/// </remarks>
public enum TerminalTransferMethod
{
    /// <summary>SFTP 传输队列(独立通道,不占终端;仅 SSH 会话可用)。</summary>
    Sftp,

    /// <summary>终端内 ZMODEM。</summary>
    ZModem,

    /// <summary>终端内 YMODEM。</summary>
    YModem,

    /// <summary>终端内 XMODEM(具体用 128 还是 1K 块由块大小设置决定)。</summary>
    XModem
}
