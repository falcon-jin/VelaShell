using System.Text;
using Avalonia.Input.TextInput;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 输入法组字串(预编辑)在终端里的落点与列布局。
/// <para>
/// 回归的故障:终端的 IME 客户端把 <c>SupportsPreedit</c> 报成 false,而 Avalonia 的 Win32 后端
/// 从不让输入法自己画组字窗(<c>Imm32InputMethod.ShowCompositionWindow</c> 恒为 false,它只用
/// <c>ImmSetCandidateWindow</c> 摆候选窗的位置)—— 组字串<b>唯一</b>的出口
/// <c>Client.SetPreeditText</c> 被那个 false 挡住,于是拼音被静默丢弃:候选窗浮在光标旁,
/// 用户却看不见自己敲进去的是什么,打错了也不知道该退几下。
/// </para>
/// <para>
/// 因此本文件一律经 <c>ImeClientForTest</c>(走平台索要客户端的那条真路)喂组字串,
/// 而不是去调控件的私有方法 —— 把 <c>SupportsPreedit</c> 一起锁在断言里。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Ime")]
public sealed class ImePreeditTests
{
    /// <summary>全程序集共用的 headless 会话(见 HeadlessTestSession)。</summary>
    private static Avalonia.Headless.HeadlessUnitTestSession Session => HeadlessTestSession.Current;

    private static void OnUi(Action<VelaTerminalControl, TextInputMethodClient> body) =>
        Session
            .Dispatch(
                () =>
                {
                    using var control = new VelaTerminalControl();
                    control.Feed(Encoding.ASCII.GetBytes("$ "));
                    TextInputMethodClient client =
                        control.ImeClientForTest
                        ?? throw new AssertFailedException(
                            "终端没有提供 IME 客户端 —— 输入法在这个控件上根本不会工作。");
                    body(control, client);
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

    [TestMethod]
    public void Client_SupportsPreedit_SoThePlatformDoesNotDropTheCompositionString() =>
        OnUi(
            (_, client) =>
                Assert.IsTrue(
                    client.SupportsPreedit,
                    "报 false 时 Imm32InputMethod.CompositionChanged 会把组字串直接丢掉 —— "
                        + "屏幕上不显示拼音,用户退格时不知道该退几下。"));

    [TestMethod]
    public void SetPreeditText_IsHeldForDisplay() =>
        OnUi(
            (control, client) =>
            {
                client.SetPreeditText("ni'hao", 6);
                Assert.AreEqual("ni'hao", control.PreeditTextForTest);
            });

    [TestMethod]
    public void EmptyOrNullPreedit_EndsComposition() =>
        OnUi(
            (control, client) =>
            {
                client.SetPreeditText("ni", 2);
                client.SetPreeditText(null, null);
                Assert.IsNull(control.PreeditTextForTest);

                // 部分后端用空串而非 null 表示"组字结束",两者必须等价,
                // 否则会留下一段 0 字符的组字态:光标让位给了一个画不出东西的叠加层。
                client.SetPreeditText("ni", 2);
                client.SetPreeditText(string.Empty, 0);
                Assert.IsNull(control.PreeditTextForTest);
            });

    [TestMethod]
    public void CaretColumn_FollowsTheCompositionCaret() =>
        OnUi(
            (control, client) =>
            {
                client.SetPreeditText("nihao", 2);
                Assert.AreEqual(2, control.PreeditCaretColumnForTest);
            });

    [TestMethod]
    public void CaretColumn_CountsWideCharactersAsTwoCells() =>
        OnUi(
            (control, client) =>
            {
                // 日文输入法在选字前就把罗马字转成了假名:插入点在第 2 个字符之后,
                // 而屏幕上那是第 4 列 —— 列数按 CharWidth 算,和提交后的回显同一套度量,
                // 否则候选一上屏,插入符会横向跳一下。
                client.SetPreeditText("にほんご", 2);
                Assert.AreEqual(4, control.PreeditCaretColumnForTest);
            });

    [TestMethod]
    public void MissingCaret_PutsItAtTheEnd() =>
        OnUi(
            (control, client) =>
            {
                // X11 等后端只给组字串、不给串内插入点。
                client.SetPreeditText("ni'hao");
                Assert.AreEqual(6, control.PreeditCaretColumnForTest);
            });

    [TestMethod]
    public void CompositionRect_SpansTheWholeCompositionSoTheCandidateWindowClearsIt() =>
        OnUi(
            (control, client) =>
            {
                double cell = client.CursorRectangle.Width;
                client.SetPreeditText("ni'hao", 6);
                Assert.AreEqual(
                    6 * cell,
                    client.CursorRectangle.Width,
                    "Avalonia 把这个矩形当候选窗的 CFS_EXCLUDE 排除区;只报一个格子的话,"
                        + "候选窗会直接压在拼音上。");

                // 命令补全弹层用的锚点仍是单格,不受组字影响。
                Assert.AreEqual(cell, control.GetCursorRect().Width);
            });

    [TestMethod]
    public void PreeditIsNotSentToThePty() =>
        OnUi(
            (control, client) =>
            {
                var sent = new List<byte>();
                control.UserInput += bytes => sent.AddRange(bytes);
                client.SetPreeditText("ni'hao", 6);
                Assert.IsEmpty(sent, "组字串还没提交,一个字节都不该下发 —— 提交由 OnTextInput 单独送达。");
            });

    [TestMethod]
    public void DisablingIme_ClearsAStuckComposition() =>
        OnUi(
            (control, client) =>
            {
                client.SetPreeditText("ni'hao", 6);
                control.ImeEnabled = false;
                Assert.IsNull(
                    control.PreeditTextForTest,
                    "关掉输入法后平台不会再回调收尾,不清就永远挂在光标上。");
            });
}
