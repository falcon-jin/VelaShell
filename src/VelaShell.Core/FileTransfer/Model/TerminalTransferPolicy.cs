namespace VelaShell.Core.FileTransfer.Model;

/// <summary>
/// 一条会话最终生效的终端内传输策略:三种协议各自的启停、默认传输方式,以及两项协议参数。
/// </summary>
/// <remarks>
/// <para>
/// 这是「全局设置」与「本条连接的覆盖项」合成之后的结果(见 <c>SessionTransferSettings.Resolve</c>),
/// 不可变。路由器、命令面板的可用性判定、通用收发命令都只看它,谁都不再自己去拼
/// <c>profile.Transfer?.X ?? settings.Transfer.Y</c> —— 那种写法散在五六处,漏一处的表现是
/// 「关了 ZMODEM,新建标签不接管了,重连之后又接管了」,而那种 bug 没人会往这里想。
/// </para>
/// <para>
/// 默认值等于「三种协议全开、默认走 SFTP」,与本功能上线之前的行为一致:自动接管照旧,
/// 新增的只是「可以关」。
/// </para>
/// </remarks>
public sealed record TerminalTransferPolicy
{
    /// <summary>
    /// 是否允许 ZMODEM。关闭后路由器<b>不再嗅探输出流</b>,远端 <c>sz</c>/<c>rz</c> 的引导字节
    /// 原样显示在终端里(与 SecureCRT 的 Disable Zmodem、CxShell 的自动激活开关同义)。
    /// </summary>
    public bool ZModem { get; init; } = true;

    /// <summary>是否允许 YMODEM / YMODEM-G(命令行武装与命令面板入口一并生效)。</summary>
    public bool YModem { get; init; } = true;

    /// <summary>是否允许 XMODEM / XMODEM-1K。</summary>
    public bool XModem { get; init; } = true;

    /// <summary>
    /// 「发送 / 接收文件」这对通用命令走哪条路。已由解析器算准:
    /// 选了 SFTP 但这条连接没有 SFTP 通道、或选了某个已被禁用的协议时,这里是退回之后的结果。
    /// </summary>
    public TerminalTransferMethod DefaultMethod { get; init; } = TerminalTransferMethod.Sftp;

    /// <summary>
    /// 默认方式为 ZMODEM 时,「发送文件」注入到远端 shell 的命令。
    /// </summary>
    /// <remarks>
    /// 注入之后就交给既有的自动接管:远端 <c>rz</c> 吐出 ZRINIT,检测器命中,我们转为发送方。
    /// 默认 <c>rz -E</c> 与 Xshell / CxShell 一致(<c>-E</c> = 重名时由接收端改名,不覆盖)。
    /// 可配是因为有些堡垒机把 <c>rz</c> 包成了别的名字或要带固定参数。
    /// </remarks>
    public string ZModemUploadCommand { get; init; } = DefaultZModemUploadCommand;

    /// <summary>
    /// 默认方式为 XMODEM 时,发送使用的数据块负载字节数:128(经典)或 1024(XMODEM-1K)。
    /// </summary>
    /// <remarks>
    /// 只影响<b>发送</b>方向,也只影响 XMODEM:接收方向按对端发来的 SOH / STX 自适应,
    /// 而我们的 YMODEM 引擎按规范恒为 1K。命令面板里那几条按变体写死的入口不受它影响 ——
    /// 那些条目的名字里就写着用哪个变体。
    /// </remarks>
    public int XModemBlockSize { get; init; } = DefaultXModemBlockSize;

    /// <summary>ZMODEM 上传命令的出厂默认值。</summary>
    public const string DefaultZModemUploadCommand = "rz -E";

    /// <summary>XMODEM 发送块大小的出厂默认值(1K)。</summary>
    public const int DefaultXModemBlockSize = 1024;

    /// <summary>出厂默认策略:三种协议全开、默认走 SFTP。</summary>
    public static TerminalTransferPolicy Default { get; } = new();

    /// <summary>该协议变体当前是否被允许;变体跟随各自的父协议。</summary>
    /// <param name="protocol">要判定的协议变体。</param>
    /// <returns>允许时为 true。</returns>
    public bool Allows(TerminalTransferProtocol protocol) =>
        protocol switch
        {
            TerminalTransferProtocol.ZModem => ZModem,
            TerminalTransferProtocol.YModem or TerminalTransferProtocol.YModemG => YModem,
            TerminalTransferProtocol.XModem or TerminalTransferProtocol.XModem1K => XModem,
            _ => false
        };

    /// <summary>
    /// 默认方式为 XMODEM 时,「发送文件」实际使用的变体(按 <see cref="XModemBlockSize" /> 选)。
    /// </summary>
    /// <returns>128 字节块时为经典 XMODEM,否则为 XMODEM-1K。</returns>
    public TerminalTransferProtocol ResolveXModemVariant() =>
        XModemBlockSize == 128 ? TerminalTransferProtocol.XModem : TerminalTransferProtocol.XModem1K;

    /// <summary>
    /// <see cref="DefaultMethod" /> 对应的协议变体;方式为 <see cref="TerminalTransferMethod.Sftp" />
    /// 时为 null(那条路不走终端内协议)。
    /// </summary>
    /// <returns>协议变体,或 null。</returns>
    public TerminalTransferProtocol? DefaultProtocol() =>
        DefaultMethod switch
        {
            TerminalTransferMethod.ZModem => TerminalTransferProtocol.ZModem,
            TerminalTransferMethod.YModem => TerminalTransferProtocol.YModem,
            TerminalTransferMethod.XModem => ResolveXModemVariant(),
            _ => null
        };
}
