using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.Models;

/// <summary>
/// 一条连接配置对全局「终端内文件传输」设置的覆盖项;每个字段 null = 跟随全局。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="TerminalOverrides" /> 同构、同理由:全局只能有一套,但机器不是一样的 ——
/// 串口 / 嵌入式那几台就指着 X/YMODEM 过日子,而绝大多数 SSH 机器上这两种协议慢且脆
/// (停等式每块一个 RTT,链路上又没有可识别的引导序列),开着只是多一份误触发的风险。
/// 挤进一个全局开关里就只能二选一。
/// </para>
/// <para>
/// <b>单开一个对象而不是并进 <see cref="TerminalOverrides" /></b>:那个对象的界面只对 SSH 显示,
/// 而最需要按连接配置 X/YMODEM 的恰恰是没有 SFTP 通道的插件终端协议(Telnet / 串口)。
/// 分开之后两处的显示条件可以各走各的。落盘上两者都是 <c>null</c>,老配置零迁移。
/// </para>
/// </remarks>
public sealed class TransferOverrides
{
    /// <summary>是否启用 ZMODEM 自动接管;null = 跟随全局。</summary>
    public bool? ZModemEnabled { get; set; }

    /// <summary>是否启用 YMODEM / YMODEM-G;null = 跟随全局。</summary>
    public bool? YModemEnabled { get; set; }

    /// <summary>是否启用 XMODEM / XMODEM-1K;null = 跟随全局。</summary>
    public bool? XModemEnabled { get; set; }

    /// <summary>
    /// 「发送 / 接收文件」这对通用命令默认走哪条路;null = 跟随全局。
    /// </summary>
    /// <remarks>
    /// 存的是用户<b>选了什么</b>,不是最终生效值:选了 SFTP 但这条连接没有 SFTP 通道时的退回
    /// 由 <see cref="SessionTransferSettings.Resolve" /> 在运行时算,不写回配置 ——
    /// 把退回结果存进配置,用户以后给这条连接换了协议类型就再也回不到 SFTP 了。
    /// </remarks>
    public TerminalTransferMethod? DefaultMethod { get; set; }

    /// <summary>是否一项都没覆盖(等价于整个对象为 null)。</summary>
    /// <remarks>
    /// 界面把四项都设回「跟随全局」之后应当存回 <c>null</c> 而不是一个全空对象:后者会让
    /// 每条老配置的落盘 JSON 平白多出一段,也让「有没有覆盖」这件事有了两种表示。
    /// </remarks>
    public bool IsEmpty =>
        ZModemEnabled is null
        && YModemEnabled is null
        && XModemEnabled is null
        && DefaultMethod is null;

    /// <summary>返回本对象的副本。</summary>
    /// <returns>与本实例等值的新实例。</returns>
    public TransferOverrides Clone() =>
        new()
        {
            ZModemEnabled = ZModemEnabled,
            YModemEnabled = YModemEnabled,
            XModemEnabled = XModemEnabled,
            DefaultMethod = DefaultMethod
        };
}
