# 终端内文件传输(X / Y / ZMODEM)协议开关与默认协议 —— 整改方案

> 状态:**P1 已实现**(2026-09-14 起草并落地)。§1~§5 是当初的分析与选型,原样保留;
> §6 起记录实际做了什么、与方案有哪些出入、还剩什么。
> 起因:X/YMODEM 实际使用中不稳且慢,ZMODEM 略好但也不顺手;日常更倾向 SFTP。
> 诉求:在**连接设置**里能分别启用 / 禁用三种协议,并选一个**默认协议**(如 ZMODEM)。

---

## 0. 结论先行

| 决策点 | 建议 | 一句话理由 |
| --- | --- | --- |
| 配置分层 | **全局默认(设置 → 文件传输)+ 每条连接可覆盖(连接对话框 → 高级)** | 与现有 `TerminalOverrides` 的「null = 跟随全局」模式完全同构;串口 / 嵌入式那几台要 X/YMODEM,其余机器想全关,全局单开关做不到 |
| 开关粒度 | 三个独立布尔:`ZModem` / `YModem` / `XModem` 各自启用 | XMODEM-1K、YMODEM-G 是变体不是协议,跟着父协议走;位掩码没有可读性优势 |
| 「默认协议」的含义 | **默认传输方式** `Sftp / ZModem / YModem / XModem`,SSH 会话默认 **SFTP**,无 SFTP 通道的传输层(插件串口 / Telnet / 本地 ConPTY)自动退回 ZMODEM | 一个没有消费者的"默认值"就是骗人的设置(仓库内 R-09 的教训);它的消费者是新增的一对通用命令「发送文件到远端…」「从远端接收文件…」 |
| 禁用后的行为 | ZMODEM 禁用 = **不再嗅探输出流**(引导字节原样进终端,同 CxShell / SecureCRT 「Disable Zmodem」);X/Y 禁用 = 命令行武装与命令面板入口一起失效 | 零开销、行为可预期 |
| 默认值 | 三个协议**默认全部启用**,默认传输方式 SFTP | 不改存量用户的 `sz`/`rz` 肌肉记忆;不想要的人在连接上关 |
| 现有 6 条按协议的命令面板入口 | 保留,按启用状态置灰 | 面向高级用户的"救援"入口,与通用命令不冲突 |

---

## 1. 现状盘点

代码位置以本仓为准(2026-09-14,`dev`)。

### 1.1 协议引擎与路由(已完备,无需重写)

| 层 | 文件 | 现状 |
| --- | --- | --- |
| Core 引擎 | `src/VelaShell.Core/ZModem/`、`src/VelaShell.Core/XYModem/` | 三种协议全自研,收发双向,传输无关;`ZModemOptions`(8K 子包、握手 5s×3、帧超时 30s×10)、`XYModemOptions`(握手 3s×10、块超时 20s×10) |
| 路由器 | `src/VelaShell.Terminal/FileTransfer/TerminalTransferRouter.cs` | ZMODEM **自动接管**(`ZModemDetector` 识别完整十六进制帧头且尾锚定);X/YMODEM **只能手动**:① 命令行武装(`TransferCommandParser` 识别用户敲的 `sx/rx/sb/rb`,回车落流后再启动)② 命令面板手动启动 |
| 桥 | `src/VelaShell.Terminal/SshTerminalBridge.cs` | 读循环先过路由器(常态零拷贝直通);会话期间击键只认 Ctrl+C / Ctrl+X 为取消;`SendRaw` 程序化注入不经终端控件输入事件 |
| 宿主接线 | `src/VelaShell/ViewModels/TerminalTabViewModel.cs` `AttachTransferRouter` | 只要下载目录选择器 + 传输面板 + 设置委托三者就绪就**无条件装路由器**,硬编码 `ZModemOptions.Default`;SSH、本地 ConPTY、插件协议(Telnet / 串口)四条 `AttachTransport` 入口共用 |
| 命令面板 | `MainWindowViewModel.RegisterManualTransferCommand` | 6 条:YMODEM 收 / 发、YMODEM-G 收、XMODEM 收 / 发、XMODEM-1K 发;可用性 = 有路由器 && 无会话 && (接收方向或已接线上传) |
| 落地 / 选源 | `src/VelaShell/Services/FileTransfer/` | 下载走原生目录选择框(`FolderTransferFileSink`,冲突策略读 `AppSettings.Transfer.ConflictPolicy`),上传走原生文件选择框 |

### 1.2 配置面(完全空白)

- `AppSettings.Transfer`(`TransferOptions`)全部面向 SFTP 队列:下载目录、并发、冲突、限速、日志、远程编辑。**没有任何 X/Y/ZMODEM 项**。
- `SessionProfile` 有 `TerminalOverrides? Terminal`(编码 / TERM / 配色 / 标签色 / 起始目录 / 保活 / 防空闲),`null = 跟随全局`,解析器 `SessionTerminalSettings`。**没有传输相关覆盖**。
- 连接对话框 `ConnectionProfileView.axaml` 「高级」区:标签、跳板、认证后命令、会话级终端覆盖(仅 SSH 显示)。
- 设置页 `Views/Settings/TransferSettingsPage.axaml`:六个小节(默认路径 / 远程编辑 / 行为 / 冲突 / 带宽 / 日志)。

### 1.3 SFTP 侧(已经是更强的那条路)

- 双栏浏览、拖拽互传、断点续传、传输队列与限速、远程编辑回传、**「跟随终端目录」**(`FileBrowserViewModel.FollowTerminal`,依赖 #305 的目录上报钩子)。
- `FileBrowserViewModel.UploadLocalPathsAsync(paths)`:上传菜单与拖放共用的入口,可直接被"发送文件到远端(SFTP)"复用。
- 终端标签已跟踪 `TerminalWorkingDirectory`(OSC 7 / 目录上报)。

---

## 2. 为什么 X/YMODEM「不稳且慢」、ZMODEM「稍好」、SFTP 该是首选

这一节是给方案定调的:**问题大半是协议本身的,不是引擎实现的**,所以整改重心是"让用户少碰它、碰的时候可控",而不是继续调引擎参数。

### 2.1 慢:停等协议撞上高延迟链路

| 协议 | 推进方式 | 每块数据 | 50 ms RTT 上的理论上限 | 200 ms RTT |
| --- | --- | --- | --- | --- |
| XMODEM | 每块等 ACK(停等) | 128 B | ≈ 2.5 KB/s | ≈ 0.6 KB/s |
| XMODEM-1K / YMODEM | 每块等 ACK | 1 KB | ≈ 20 KB/s | ≈ 5 KB/s |
| YMODEM-G | 流式,不等 ACK | 1 KB | 受限于 PTY 带宽 | 同左 |
| ZMODEM | 流式(ZCRCG 子包),对端只在出错时插话 | 8 KB 子包(本仓默认) | 受限于 PTY 带宽 | 同左 |
| SFTP | 独立 SSH 通道,窗口化、多请求并发 | 32 KB 请求 × 多个在途 | 接近链路带宽 | 接近链路带宽 |

X/YMODEM 在 SSH 这种几十到几百毫秒 RTT 的链路上**天生就是这个速度**,任何实现都一样;它们的主场是串口(RTT ≈ 0)。

### 2.2 不稳:X/YMODEM 在链路上没有身份

- **没有引导序列**:`sb`/`sx` 启动后静默等接收方发 `'C'`,`rb`/`rx` 只吐裸 `'C'`,与普通字符无异。本仓只能靠**用户敲的命令行**来武装会话(`TransferCommandParser`),于是 alias、脚本、`make upload`、带管道的命令、行编辑过的命令行,任何一种都可能让跟踪器认不出来 → 表现为"敲完命令毫无反应"或"'C' 被 shell 原样回显"。
- **命令行 → 回车落流 → 引擎握手**是三段接力(见 `SshTerminalBridge` 的 `StartPendingTransferAfterWritesAsync` 注释),中间任何时序偏差都直接导致握手失败,然后是 30 秒黑屏等超时。
- **XMODEM 不带文件名**:`rx` 不给文件名就注定失败,只能靠命令行解析兜底。
- 没有断点续传、没有跳过语义(Skip 退化为中止)、单次 CRC 失败要重传整块。

### 2.3 ZMODEM「稍好但不顺手」

- 协议本身没问题(流式、CRC32、能续传),引擎参数也已对齐 SecureCRT / Xshell(8K 子包)。
- 不顺手主要来自**交互**:每次 `sz` 都弹目录选择框、每次 `rz` 都弹文件选择框;握手不成时终端黑 15 秒;引导被分片切开时屏幕上会留一两个 `*`。这些是可以通过"默认下载目录、不弹框"和"默认传输方式改 SFTP"绕开的。
- 在**终端内传输**这个类别里,ZMODEM 是三者中唯一值得默认开着的。

### 2.4 SFTP 为什么该是 SSH 会话的默认

独立通道(不占终端、不会吞击键)、窗口化并发、断点续传、目录递归、限速、日志、冲突策略、跟随终端目录 —— 全部**已经做好了**。终端内协议在 SSH 上的唯一优势是"人在远端 shell 里敲一条命令就能触发",这一点靠「跟随终端目录」+ 一条通用命令就能补上。

---

## 3. 参考实现对比

| 产品 | 终端内协议 | 配置层级 | 关键选项 | 默认协议 / 主路径 | 对本仓的启示 |
| --- | --- | --- | --- | --- | --- |
| **Xshell** | X / Y / ZMODEM | **会话属性 → 文件传输**(含 X/YMODEM、ZMODEM 两个子页) | 上传协议(单选);X/YMODEM:包大小 128 / 1K;ZMODEM:**Activate ZMODEM automatically**、上传命令(`rz -E`)、下载路径;菜单 文件 → 传输 → 用 X/YMODEM 接收 / 发送 | 默认 ZMODEM;X/Y 必须手动从菜单发起 | 会话级配置 + "自动激活"开关 + 上传命令可配 这三件是行业惯例 |
| **SecureCRT** | X / Y / ZMODEM(+ Kermit) | **Session Options → Terminal → X/Y/Zmodem** | X/Ymodem 包大小;**Disable Zmodem** 复选框;上传 / 下载目录(按协议分);拖文件到终端窗口触发 Zmodem 上传(官方 Tips,细节未逐项核实) | 自动检测 Zmodem 并下载 | "禁用 = 不再检测"的语义;目录按协议分是过度设计,本仓复用全局下载目录即可 |
| **MobaXterm** | 有 ZMODEM 能力,但**官方文档没有任何 ZMODEM 配置项** | — | — | 主路径是 SSH-browser(SFTP / SCP)侧栏 + 拖拽 | 与本仓"SFTP 为主"的方向一致 |
| **Termius** | **没有** X/Y/ZMODEM | — | — | 只有 SFTP | 现代客户端可以不做终端内协议;本仓已做,那就让它可关 |
| **meatshell**(Rust + Slint) | 仅 ZMODEM(`sz` 下载 / `rz` 多文件上传) | 无配置 | 文件落到系统下载目录,不弹框;会话结束把残余字节交还终端 | ZMODEM 唯一 | "不弹框直接落下载目录"值得作为可选行为 |
| **termora**(Kotlin) | 仅 ZMODEM 接线(库里有 X/Y 类但未用) | 无配置 | `PtyConnectorDelegate` 只匹配 `**<ZDLE>` 三字节就接管(比本仓的完整帧头判据粗);每次弹文件框;进度直接写进终端;Ctrl+C 取消 | ZMODEM 唯一 | 反面教材:三字节判据会误触发,本仓的检测器不要退化 |
| **CxShell**(.NET + Avalonia,与本仓最像) | X / Y / ZMODEM,纯 C# | **会话级**(`SessionInfo`),会话编辑对话框里「文件传输 → X/YMODEM、ZMODEM」两页(Xshell 布局) | `FileTransferUploadProtocol = Auto/Xmodem/Ymodem`;`FileTransferXymodemBlockSize = 128/1024`;`FileTransferXmodemUploadCommand = "rx"`;`FileTransferYmodemUploadCommand = "rb -E"`;**`FileTransferZmodemAutoActivate = true`**(为 false 时输出流扫描整段跳过);`FileTransferZmodemUploadCommand = "rz -E"`;归一化函数带单测(非法值回退 Auto) | Auto(= 只靠用户敲命令触发) | 字段设计可直接借鉴;但它**没有全局层**,每条会话都得配一遍,这正是本仓 `TerminalOverrides` 模式要避免的 |

综合:**Xshell / SecureCRT 的会话级配置模型 + 本仓自己的「全局默认 + null 覆盖」两层结构 + SFTP 作为 SSH 默认方式**。

---

## 4. 方案设计

### 4.1 设计原则(全部来自本仓既有约定)

1. **null = 跟随全局**,不用哨兵值(`TerminalOverrides` 注释里的理由:哨兵分不清"明确选了"与"没选")。
2. **每个持久化字段必须有运行时消费者**(设置审计 R-09 把"只持久化不消费"列为缺陷)。
3. **加字段只改 `SessionProfile.Clone` 一处**,`SessionProfileCloneTests` 反射逐属性比对会兜底。
4. **传输无关**:路由器策略对 SSH / ConPTY / 插件协议一视同仁;SFTP 只在有 SFTP 通道的会话上可选。
5. **改默认值 = 动存量用户**:三种协议默认全开,行为与今天完全一致;新增的只是"可以关"。

### 4.2 数据模型

#### Core:枚举

`src/VelaShell.Core/FileTransfer/Model/FileTransferEnums.cs` 新增:

```csharp
/// <summary>通用「发送 / 接收文件」命令使用的传输方式。</summary>
public enum TerminalTransferMethod
{
    /// <summary>SFTP 队列(仅 SSH 会话可用;其余传输层按启用情况退回 ZMODEM)。</summary>
    Sftp,
    ZModem,
    YModem,
    XModem
}
```

不复用 `TerminalTransferProtocol`:那个枚举含 `XModem1K` / `YModemG` 变体,是引擎参数;"默认方式"是用户意图,粒度不同,而且要多一个 `Sftp`。

#### Core:全局默认(`AppSettings.Transfer`)

`TransferOptions` 追加一个小节(命名沿用现有 `ObservableOptions` 风格):

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `TerminalZModemEnabled` | bool | true | ZMODEM 自动接管(嗅探 `sz`/`rz` 引导) |
| `TerminalYModemEnabled` | bool | true | YMODEM / YMODEM-G(命令行武装 + 面板入口) |
| `TerminalXModemEnabled` | bool | true | XMODEM / XMODEM-1K |
| `TerminalDefaultMethod` | `TerminalTransferMethod` | `Sftp` | 通用命令用哪种方式 |
| `TerminalZModemUploadCommand` | string | `"rz -E"` | 通用「发送」在 ZMODEM 方式下注入的命令(P2) |
| `TerminalXYModemBlockSize` | int | 1024 | 发送时 128 / 1024,决定 XMODEM vs XMODEM-1K(P2;接收方向按 SOH/STX 自适应,无需配置) |

`AppSettings.Normalize()` 里补钳位:非法枚举回退 `Sftp`,块大小只认 128 / 1024,上传命令空白回退默认。

#### Core:会话级覆盖(`SessionProfile`)

新建 `src/VelaShell.Core/Models/TransferOverrides.cs`,与 `TerminalOverrides` 同构:

```csharp
public sealed class TransferOverrides
{
    public bool? ZModemEnabled { get; set; }
    public bool? YModemEnabled { get; set; }
    public bool? XModemEnabled { get; set; }
    public TerminalTransferMethod? DefaultMethod { get; set; }
    public bool IsEmpty => ...;   // 四项全 null
    public TransferOverrides Clone() => ...;
}
```

`SessionProfile` 增加 `public TransferOverrides? Transfer { get; set; }`,`Clone()` 里加一行 `Transfer = Transfer?.Clone()`。

**为什么不塞进 `TerminalOverrides`**:那个对象的 UI 只对 SSH 显示,而 X/YMODEM 最需要按连接配置的恰恰是插件串口 / Telnet 这类没有 SFTP 的终端协议;分开之后显示条件可以独立("有终端的连接类型"而不是"SSH")。落盘上二者都是 `null` 零迁移。

#### Core:解析器

新建 `src/VelaShell.Core/Models/SessionTransferSettings.cs`(镜像 `SessionTerminalSettings`):

```csharp
public static class SessionTransferSettings
{
    public static TerminalTransferPolicy Resolve(SessionProfile? profile, AppSettings settings, bool hasSftp);
}

/// <summary>路由器与通用命令消费的、已经算好的策略。</summary>
public sealed record TerminalTransferPolicy(
    bool ZModem, bool YModem, bool XModem,
    TerminalTransferMethod DefaultMethod,
    string ZModemUploadCommand, int XYModemBlockSize)
{
    public bool AnyProtocol => ZModem || YModem || XModem;
    public bool Allows(TerminalTransferProtocol protocol) => protocol switch { ... };
    public static TerminalTransferPolicy Default { get; } = ...;
}
```

`Resolve` 的退回规则(**这是"默认协议"最需要讲清楚的一条**):
`DefaultMethod == Sftp && !hasSftp` → 按 ZModem → YModem → XModem 的顺序取第一个已启用的;都没启用 → 通用命令置灰。本地终端与插件协议 `hasSftp = false`。

### 4.3 运行时门控(路由器策略)

`TerminalTransferRouter` 构造增加 `TerminalTransferPolicy policy`(默认 `Default`,现有测试零改动):

| 入口 | ZMODEM 禁用时 | X/Y 禁用时 |
| --- | --- | --- |
| `CanPassThrough` | 恒 true(检测器不参与) | 不变 |
| `ProcessIncoming` | 跳过 `_detector.Process`,字节原样喂终端 | 不变 |
| `NoteCommandSubmitted` | `sz`/`rz` 不再放宽判据 | `sx/rx/sb/rb` 不武装,返回 false |
| `StartManualSession` | 返回新增的 `TransferStartFailure.ProtocolDisabled` | 同左 |
| `TerminalTabViewModel.AttachTransferRouter` | — | **照常装**路由器,由策略在内部门控(实现时的改动,理由见 §6.1) |

策略从哪来:`MainWindowViewModel` 在 `WireZModemDownload`(建议顺手改名 `WireTerminalTransfer`)里算好 `SessionTransferSettings.Resolve(profile, settings, hasSftp)` 赋给 `terminalTab.TransferPolicy`;`AttachTransferRouter` 读它。重连走同一入口,自然重算。

设置页改动后**对已打开标签的生效时机**:P1 = 下次连接 / 重连生效(与 `TerminalOverrides` 里编码、TERM 的行为一致);P2 可在 `OnSettingsSaved` 里对每个标签重算并调用 `router.UpdatePolicy(...)`(路由器内部只是换一个不可变记录,持 `_gate` 即可,会话进行中不打断)。

`CanStartManualTransfer(direction)` 追加 `policy.Allows(protocol)`,6 条现有面板命令随之置灰。

### 4.4 「默认传输方式」的消费者:一对通用命令

命令面板 `CmdCat_Transfer` 分类新增两条(若终端标签日后有右键菜单,同一对命令挂上去):

| 命令 id | 标题 | Sftp | ZModem | YModem | XModem |
| --- | --- | --- | --- | --- | --- |
| `transfer.send` | 发送文件到远端… | 文件选择框 → `FileBrowserViewModel.UploadLocalPathsAsync` 上传到 `TerminalWorkingDirectory`(未知时退回会话家目录并提示) | 经 `bridge.SendRaw` 注入 `<ZModemUploadCommand>\r`,后面交给现有自动检测 | 先选文件,再注入 `rb -E\r`,并调用 `router.NoteCommandSubmitted("rb")` 武装 | 先选**单个**文件,注入 `rx <basename>\r` 并武装 |
| `transfer.receive` | 从远端接收文件… | 打开 SFTP 面板并「跟随终端目录」(现有 `ToggleSftp` + `FollowTerminal`) | `StartManualSession(ZModem, Receive)`(对端已跑 `sz` 但引导被漏掉的救援,等价 Xshell「Receive with ZMODEM」) | `StartManualSession(YModem, Receive)`(用户已敲 `sb`) | `StartManualSession(XModem, Receive)`(用户已敲 `sx`) |

**一个必须核实的时序细节**:X/Y 的"武装 → 回车落流 → 启动"接力目前挂在 `SshTerminalBridge.OnUserInput`(击键路径)里;`SendRaw` 只入队不检查 `HasPendingManualSession`。通用命令注入要么复用同一段检查(把它抽成 `EnqueueOutboundAndArm`),要么在 `SendRaw` 之后显式 `await DrainWritesAsync()` 再 `StartPendingManualSession()`。实现时二选一,并补一条路由测试。

### 4.5 界面

#### 全局:设置 → 文件传输 → 新小节「终端内传输(X / Y / ZMODEM)」

放在「行为」小节之后、「冲突」之前(与"下载目录"共用同一节语义:ZMODEM 落地目录本来就读它)。

```
终端内传输(X / Y / ZMODEM)
  默认传输方式        [SFTP ▾]          通用「发送 / 接收文件」命令使用的方式;
                                        没有 SFTP 通道的连接(串口、Telnet、本地终端)自动改用 ZMODEM
  [x] ZMODEM 自动接管   远端运行 sz / rz 时自动开始传输。关闭后引导字节会原样显示在终端里
  [x] YMODEM            允许 sb / rb 与命令面板里的 YMODEM 入口
  [x] XMODEM            允许 sx / rx 与命令面板里的 XMODEM 入口
  —— 以下为 P2 ——
  ZMODEM 上传命令       [rz -E        ]  「发送文件」在 ZMODEM 方式下注入到远端 shell 的命令
  X/YMODEM 数据块       ( ) 128 字节  (•) 1024 字节
```

#### 连接对话框 → 高级 → 新块「文件传输」

放在「会话级终端覆盖」之后。**显示条件 = 该连接类型有终端**(SSH,以及插件终端协议;不对 SFTP / FTP / 纯文件插件显示)。当前 `ConnectionProfileViewModel` 没有"插件协议是否带终端"的判定,第一步可先按 SSH 显示,插件终端协议在核实描述符能力位后补上。

```
文件传输
  只对这一条连接覆盖终端内传输设置。选「跟随全局」即沿用设置 → 文件传输里的值。
  默认传输方式   [跟随全局 ▾]      ZMODEM 自动接管  [跟随全局 ▾]
  YMODEM         [跟随全局 ▾]      XMODEM           [跟随全局 ▾]
```

**最终用了四个下拉**,而不是方案里设想的三态复选框:本仓没有 `IsThreeState` 先例,而「跟随全局 / 启用 / 禁用」三项下拉与紧邻的会话级终端覆盖(编码、TERM、配色都是「跟随全局」打头的下拉)长得一模一样,用户不必学新东西。

保存时四项全 null → 存 `null`(同 `BuildTerminalOverrides` 的 `IsEmpty` 处理)。

### 4.6 国际化

五个 resx(`Strings.resx` / `zh-Hans` / `zh-Hant` / `ja` / `ko`)同步新增,键名沿用前缀约定:

- `SetTransfer_SectionTerminal`、`SetTransfer_DefaultMethod`、`SetTransfer_DefaultMethodDesc`、`SetTransfer_ZModemAuto`、`SetTransfer_ZModemAutoDesc`、`SetTransfer_YModem`、`SetTransfer_XModem`、(P2)`SetTransfer_ZModemUploadCommand`、`SetTransfer_XYBlockSize`
- `Profile_TransferSection`、`Profile_TransferDefaultMethod`、`Profile_TransferFollowGlobal`、`Profile_TransferFollowGlobalHint`(带格式占位)
- `Cmd_TransferSend`、`Cmd_TransferReceive`、`Msg_TransferMethodUnavailable`(所有方式都被禁用时的提示)
- `Method_Sftp` / `Method_ZModem` / `Method_YModem` / `Method_XModem`(下拉项)

### 4.7 兼容与迁移

- `SessionProfile.Transfer` 为 null 零迁移;`TransferOptions` 新字段有默认值,旧 JSON 反序列化即得默认。
- 云同步 / 导入导出若按属性反射或整对象序列化则自动带上;`SessionImportWriter` 与三个导入器不产生该字段(保持 null),Xshell 导入器日后可选把 `.xsh` 里的 ZMODEM 自动激活位映射过来(P3,未核实其配置键名)。
- 插件 SDK 是 NuGet 包,不动;插件协议只是通过 `hasSftp = false` 参与退回规则。

---

## 5. 备选方案与取舍

| 备选 | 为什么不选 |
| --- | --- |
| 只做全局开关 | 串口 / 嵌入式机器要 X/YMODEM,SSH 机器想全关,全局单值二选一 —— 与 `TerminalOverrides` 诞生的理由一模一样 |
| 只做会话级(CxShell 的做法) | 几十条连接每条配一遍;新建连接又是默认全开 |
| 用位掩码 `[Flags] enum` 存三个开关 | 可空覆盖要表达"这一位跟随全局、那一位覆盖"就得再配一个掩码,不如三个 `bool?` 直白 |
| 把「默认协议」做成 `TerminalTransferProtocol`(含 1K / G 变体) | 那是引擎参数;用户选"默认协议"时想的是"走 SFTP 还是走 ZMODEM",变体属于 P2 的块大小选项 |
| 默认关掉 X/YMODEM(既然不稳) | 改默认值等于改存量用户行为;而且 X/Y 只在用户主动敲 `sx/rx/sb/rb` 时才介入,"开着"本身零打扰 |
| 「默认方式」只存不用,等以后再接消费者 | 仓库审计明确把这种字段记为缺陷(R-09);没有 §4.4 的通用命令,这个选项就不该出现在界面上 |
| ZMODEM 禁用时仍嗅探、只是命中后不接管 | 白付检测成本;且用户明确关掉了却还有一份"检测到了但不管"的状态,徒增歧义 |

---


## 6. 实施记录(2026-09-14)

P1 全部落地,方案里划为 P2 的两项协议参数也一并做了(它们被 §4.4 的通用命令直接消费,不做就得在命令里写死)。
全仓 `dotnet build` 零警告零错误;测试全绿(Core 545、Terminal 499、App 1371、Infrastructure 472、Presentation 69、Controls 13、Plugin.Ai 587)。
过了一轮对抗式代码评审,发现并修掉六处问题,见 §6.4。

### 6.1 与方案的三处出入

**一、三种协议全关时仍然装路由器**(方案原写"根本不装")。
不装的话,用户在设置页把协议重新打开之后就没有东西能接住新策略 —— 于是「关掉再打开」与「一直开着」两条路径结果不同,而那种不对称没人能想明白。现在改为照常装、由策略在内部门控:ZMODEM 关掉时 `CanPassThrough` 直接放行(检测器根本不跑),常态只多一次无竞争的加锁判断,纳秒级。换来的是 §6.2 的热更新永远成立。

**二、连接对话框用四个下拉,不是三态复选框。**
本仓没有 `IsThreeState` 先例,而「跟随全局 / 启用 / 禁用」三项下拉与紧邻的会话级终端覆盖(编码、TERM、配色)长得一模一样,用户不必学新东西。

**三、连接对话框的「文件传输」块目前只对 SSH 显示。**
这是一个**已知缺口**:插件提供的终端协议(Telnet / 串口)其实最用得上它 —— 它们没有 SFTP 通道,只能靠终端内协议传文件。但对话框拿不到「这个插件协议带不带终端」这个信息:协议页签 `PluginProtocolTab` 不带该能力位(`Tabs` 合并时对已注册协议一律填 `PluginConnectionKind.FileSystem`),要拿到得走异步的 `ResolveAsync`,还可能触发插件惰性激活,在一个下拉的显隐判断里做这件事不合适。在补上那个能力位之前,这些连接**跟随全局设置**,而全局那套对它们是完整生效的(解析器按 `hasSftp = false` 退回终端内协议)。见 §7。

### 6.2 实际改了什么

| # | 改动 | 文件 |
| --- | --- | --- |
| 1 | `TerminalTransferMethod` 枚举 | `Core/FileTransfer/Model/FileTransferEnums.cs` |
| 2 | `TerminalTransferPolicy` 不可变记录:三个开关 + 默认方式 + 两项协议参数,`AnyProtocol` / `Allows` / `DefaultProtocol` / `ResolveXModemVariant` | `Core/FileTransfer/Model/TerminalTransferPolicy.cs`(新) |
| 3 | `TransferOverrides`(四个可空字段 + `IsEmpty` + `Clone`) | `Core/Models/TransferOverrides.cs`(新) |
| 4 | `SessionProfile.Transfer` + `Clone` 补一行 | `Core/Models/SessionProfile.cs` |
| 5 | `SessionTransferSettings.Resolve(profile, settings, hasSftp)`,含退回规则 | `Core/Models/SessionTransferSettings.cs`(新) |
| 6 | `TransferOptions` 六个新字段 + `Normalize` 三处钳位 | `Core/Models/AppSettings.cs` |
| 7 | 路由器接受策略并门控四处入口;新增 `Policy` / `UpdatePolicy`;`TransferStartFailure.ProtocolDisabled` | `Terminal/FileTransfer/TerminalTransferRouter.cs` |
| 8 | 标签持 `TransferPolicy`、`RefreshTransferPolicy`;`CanStartManualTransfer` 增加协议参数;路由器构造传策略 | `VelaShell/ViewModels/TerminalTabViewModel.cs` |
| 9 | `WireZModemDownload` → `WireTerminalTransfer`,新增 `ResolveTransferPolicy`;设置保存后热更新全部已开标签;注册 `transfer.send` / `transfer.receive` 及其分派 | `VelaShell/ViewModels/MainWindowViewModel.cs` |
| 10 | 设置页新小节(默认方式 + 三个开关 + 两项从属参数) | `Views/Settings/TransferSettingsPage.axaml`、`ViewModels/SettingsViewModel.cs` |
| 11 | 连接对话框「文件传输」块 + 四项往返映射 | `Views/ConnectionProfileView.axaml`、`ViewModels/ConnectionProfileViewModel.cs` |
| 12 | 26 个新键写入五份 resx;删掉两条已废弃的二次确认标题 | `Core/Resources/Strings*.resx` |

**通用命令的分派**(`StartDefaultTransferAsync`):

- **SFTP** — 把文件面板绑到当前会话、开到 `TerminalWorkingDirectory`(终端没报过目录就留在面板当前位置,不猜家目录:猜错的代价是文件传到了别处,而用户不会立刻发现);发送方向再走一次 `UploadLocalPathsAsync`。
- **ZMODEM** — 发送方向经 `SendRaw` 注入配置好的上传命令(默认 `rz -E`),随后交给既有的自动接管;接收方向手动开一次会话(对端已在跑 `sz`、引导却被漏掉时的补救)。
- **X/YMODEM** — 两个方向都只手动开会话,**不替用户注入命令**。这一族在链路上没有引导序列,「注入 → 等回车落流 → 开会话」那条三段接力本就脆(§2.2),再叠一层程序化注入只会更难查。方案 §4.4 里提的那个时序坑因此不必解:注入路径只有 ZMODEM 在走,而 ZMODEM 靠输出流嗅探接管,与回车时序无关。

### 6.3 顺带移除:文件选择框的二次确认

原先下载目录框与上传文件框都有一层"防误触":第一次取消不算数,再弹一次,第二次取消才真正中止。已删除 —— 代价是每个真心想放弃的用户都要点两次,而点错关闭按钮本来就可以再敲一次 `sz` 重来。

涉及:`TransferFolderPromptRequest.IsRetryAfterCancel` 字段删除;
`FolderTransferFileSink` 与 `PickedFilesTransferSource` 的重弹分支删除;上传选择器委托签名从 `Func<bool, CancellationToken, …>` 简化为 `Func<CancellationToken, …>`(三处:视图层实现、主窗口视图模型、标签视图模型);`ZModem_ChooseDownloadFolderRetry` 与 `ZModem_ChooseUploadFilesRetry` 两个字符串从五份 resx 删除;两个测试类改写。

### 6.4 代码评审后修掉的六处

首版实现过了一轮对抗式评审,以下问题当场修掉并各补了回归用例。

1. **会话级覆盖够不到已经开着的标签(最严重)。** 原实现在建标签时算一次策略就存住,重连走的是
   `AttachTransport` 而不是建标签那条路,于是重连会把**旧策略**重新装进新路由器 —— 用户改了设置、
   断线重连一次,发现又变回老样子。改法:标签改持一个 `TransferPolicyProvider` 委托,在**每次挂载传输时**
   自己调一次重新求值。顺带把类注释改对:唯一不会当场跟上的是**改连接配置本身**(标签持的是建标签
   那一刻的 `SessionProfile` 实例),这与同一批会话级覆盖(编码、TERM、保活)口径一致,要新开标签才生效。

2. **ZMODEM 关掉再打开会拼出假帧头。** 关着的那段时间 `CanPassThrough` 直接放行,检测器一个字节都没吃到,
   它的跨分片尾部副本就停在关掉的那一刻 —— 可能正好是半个帧头。重新打开后那段陈旧前缀会与新字节拼在一起,
   拼出一个格式良好的帧头就把普通 shell 输出当协议流交给引擎。`UpdatePolicy` 现在持锁,ZMODEM 开关一翻
   且不在会话中就 `_detector.Reset()`。**这条的回归用例验证过会红**(临时撤掉修复后 `SessionStarted` 为 true)。

3. **非 SSH 连接会偷偷存下传输覆盖。** 界面块只对 SSH 显示,但 `BuildTransferOverrides` 无条件执行 ——
   一条先按 SSH 配过「禁用 ZMODEM」、后来改成插件终端协议的配置会把那个禁用带过去,而那正是最需要
   终端内协议的一类连接,偏偏此时四个下拉已隐藏,用户看不见也改不回来。现在 `!SupportsTransferOverrides`
   一律返回 null(同「认证后执行命令」那条纪律)。

4. **终端内接收会让终端黑着不动。** 通用「接收文件」在默认方式是终端内协议时会开一个盲等的接收会话:
   接管期间终端全黑(字节归引擎、击键只认取消键),握手谈不拢还要等十几秒。现在接管前先往终端里写一行
   灰字,说明在等远端跑哪条命令、以及 Ctrl+C 可以退出来。

5. **导航失败会把「发送文件」整条路吞掉。** `NavigateToCommand` 在 `CanExecute` 为 false 时(面板正因
   跟随终端目录而列目录)不是空操作,而是 `OnError`,`.FirstAsync()` 会重新抛出 —— 而命令是
   `_ = StartDefaultTransferAsync(...)` 发起的,异常落在没人观察的 Task 上。表现为「点了发送文件,
   什么都没发生」。现在导航单独兜住(目录没跟过去不致命,照样传进面板当前目录),整个 SFTP 分支再兜一层并记一条日志。

6. **两处小的。** 会话级 `DefaultMethod` 没验枚举(手改过的值会被当成「不可用」而一路退到 ZMODEM,
   让一条 SFTP 通道好好的连接改走终端内协议),现在与全局那一项同样 `Enum.IsDefined` 一次;
   `TerminalTransferPolicy.AnyProtocol` 只有测试在用(路由器改成无条件装之后就没有生产消费者了),已删除。

---

## 7. 剩下的事

| 优先级 | 事项 | 备注 |
| --- | --- | --- |
| 🟡 | **插件终端协议的连接级覆盖**(§6.1 第三条) | 需要先给 `PluginProtocolTab` 补一个「带不带终端」的能力位,或让 `Tabs` 合并时如实填 `Kind`。改动落在插件协议注册表,与本次整改不同层,单独做 |
| 🟡 | **ZMODEM 接收「不弹框,直接落全局下载目录」** | 对"ZMODEM 不顺手"是最直接的解药(meatshell 就是这么做的),`FolderTransferFileSink` 已经在读设置,只差一个跳过选择框的分支 + 一个开关 |
| 🟢 | 把文件**拖到终端**触发 `transfer.send`(SecureCRT 行为) | `TerminalTabView` 目前没有拖放处理,得新开 |
| 🟢 | Xshell 导入器映射 ZMODEM 自动激活位 | 需先核实 `.xsh` 里的配置键名 |
| 🟢 | 状态栏 / 标签角标显示当前会话的默认传输方式 | 纯提示,可有可无 |

---

## 8. 测试

新增与改写的用例:

| 项目 | 文件 | 覆盖 |
| --- | --- | --- |
| Core | `Models/SessionTransferSettingsTests.cs`(新,12 例) | 全局 × 会话覆盖 × `hasSftp` 的解析矩阵;SFTP 无通道时的退回顺序;选中协议被禁用后的退回;三种全关时停在 SFTP;会话级方式的非法枚举回退全局;变体跟随父协议;块大小选变体;空白上传命令回退;`TransferOverrides.IsEmpty` / `Clone` |
| Core | `Models/AppSettingsNormalizeTests.cs`(追加 5 例) | 非法枚举回退 SFTP;合法值不被改;空白上传命令回退;块大小只认 128 / 1024 |
| Core | `Models/SessionProfileCloneTests.cs`(既有,反射逐属性) | 新加的 `Transfer` 字段自动纳入;漏拷即红 |
| Terminal | `TransferPolicyRouterTests.cs`(新,10 例) | 出厂默认放行全部协议;ZMODEM 关掉后引导帧头原样进终端且快路径放行(对照组:开着时接管);禁用协议手动启动返回 `ProtocolDisabled`;变体跟随父协议;禁用协议不被命令行武装(对照组:开着时武装);`UpdatePolicy` 免重连生效;换策略不打断进行中的会话;**开关翻一轮后陈旧的半个帧头不再参与拼接**(撤掉修复即红) |
| App | `ViewModels/ConnectionProfileViewModelTests.cs`(追加 4 例) | 覆盖项往返;四项全「跟随全局」存回 null;改回「跟随全局」只清那一项;换到不显示这一块的协议时整个对象不落盘 |
| App | `Services/FolderTransferFileSinkTests.cs`(改写) | 取消一次即中止、不重弹;取消后同会话不再弹;选定即接受;同会话只弹一次 |
| App | `Services/PickedFilesTransferSourceTests.cs`(改写) | 取消一次即放弃、不重弹;选定即返回;路径已不存在时跳过 |

`RealLrzszProcessTests`(WSL 真机 lrzsz)未改,默认仍是 Inconclusive;块大小选项的真机回归留待启用那套环境时跑。

---

## 9. 文档同步

- `README.md` / `README.en.md` 传输小节已补:三种协议可全局或按连接启停、默认方式、通用命令的行为。
- `velashell-docs` 的《交互与界面规格》两语版本已补:设置页新小节写进 §14 的页面表,连接对话框的「文件传输」块写进 §13.1 的高级选项(含「只对 SSH 显示是缺口而非设计」这一条)。
- `feature-plan.md` 已补一行。
- `plan.md` §12-8(ZMODEM 那一条)已补上配置面的落地说明。
