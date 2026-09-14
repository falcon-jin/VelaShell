using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.Models;

/// <summary>
/// 把「全局终端内传输设置」与「本条配置的覆盖项」合成一份生效策略。
/// </summary>
/// <remarks>
/// 与 <see cref="SessionTerminalSettings" /> 同一套路:覆盖非空就用它,否则用全局。
/// 单独成类是因为调用点有好几处(建标签、握手、重连、插件终端、设置热更新),
/// 各写一遍 <c>profile.Transfer?.X ?? settings.Transfer.Y</c> 必然漏一处。
/// </remarks>
public static class SessionTransferSettings
{
    /// <summary>
    /// 解析出这条会话最终生效的终端内传输策略。
    /// </summary>
    /// <param name="profile">会话配置;null 表示没有配置(本地终端等),此时一律跟随全局。</param>
    /// <param name="settings">全局设置。</param>
    /// <param name="hasSftp">
    /// 这条会话是否有可用的 SFTP 通道。false 时默认方式不会停在
    /// <see cref="TerminalTransferMethod.Sftp" /> 上,而是退回第一种仍启用的终端内协议 ——
    /// 否则串口 / Telnet / 本地终端上那对通用命令会永远是灰的,用户完全看不出是为什么。
    /// </param>
    /// <returns>合成后的不可变策略。</returns>
    public static TerminalTransferPolicy Resolve(SessionProfile? profile, AppSettings settings, bool hasSftp)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TransferOptions global = settings.Transfer;
        TransferOverrides? overrides = profile?.Transfer;

        bool zmodem = overrides?.ZModemEnabled ?? global.TerminalZModemEnabled;
        bool ymodem = overrides?.YModemEnabled ?? global.TerminalYModemEnabled;
        bool xmodem = overrides?.XModemEnabled ?? global.TerminalXModemEnabled;
        // 会话级覆盖同样要验一遍:全局那一项在 AppSettings.Normalize 里已经验过,而这一项
        // 躺在会话配置里,手改过的 / 来自更高版本的值一旦漏进来,ResolveMethod 会认定它「不可用」
        // 而一路退到终端内协议 —— 一条 SFTP 通道好好的 SSH 连接会莫名其妙改走 ZMODEM。
        TerminalTransferMethod chosen = overrides?.DefaultMethod is { } picked && Enum.IsDefined(picked)
            ? picked
            : global.TerminalDefaultMethod;

        return new TerminalTransferPolicy
        {
            ZModem = zmodem,
            YModem = ymodem,
            XModem = xmodem,
            DefaultMethod = ResolveMethod(chosen, hasSftp, zmodem, ymodem, xmodem),
            ZModemUploadCommand = string.IsNullOrWhiteSpace(global.TerminalZModemUploadCommand)
                ? TerminalTransferPolicy.DefaultZModemUploadCommand
                : global.TerminalZModemUploadCommand.Trim(),
            XModemBlockSize = global.TerminalXModemBlockSize == 128
                ? 128
                : TerminalTransferPolicy.DefaultXModemBlockSize
        };
    }

    /// <summary>
    /// 把用户选的默认方式落到一个此刻真正可用的方式上。
    /// </summary>
    /// <remarks>
    /// 两种需要退回的情形:① 选了 SFTP,但这条连接没有 SFTP 通道(串口、Telnet、本地终端);
    /// ② 选了某个终端内协议,但那个协议后来被关掉了(全局关的,或这条连接自己关的)。
    /// 两种都退回「第一种仍启用的终端内协议」,顺序 ZMODEM → YMODEM → XMODEM ——
    /// 这正是三者从好到差的次序(流式 + 续传 / 批量 1K / 停等 128B)。
    /// <para>
    /// 一种都不剩时停在 <see cref="TerminalTransferMethod.Sftp" /> 上:配上
    /// <c>hasSftp == false</c>,调用方据此把那对通用命令置灰即可,不必再多一个「不可用」状态。
    /// </para>
    /// </remarks>
    private static TerminalTransferMethod ResolveMethod(
        TerminalTransferMethod chosen,
        bool hasSftp,
        bool zmodem,
        bool ymodem,
        bool xmodem)
    {
        bool chosenIsUsable = chosen switch
        {
            TerminalTransferMethod.Sftp => hasSftp,
            TerminalTransferMethod.ZModem => zmodem,
            TerminalTransferMethod.YModem => ymodem,
            TerminalTransferMethod.XModem => xmodem,
            _ => false
        };
        if (chosenIsUsable)
        {
            return chosen;
        }
        if (zmodem)
        {
            return TerminalTransferMethod.ZModem;
        }
        if (ymodem)
        {
            return TerminalTransferMethod.YModem;
        }
        return xmodem ? TerminalTransferMethod.XModem : TerminalTransferMethod.Sftp;
    }
}
