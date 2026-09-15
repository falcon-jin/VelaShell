using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.TextInput;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.RenderTests;

/// <summary>
/// 输入法组字串的<b>像素级</b>回归:拼音到底画没画到屏幕上。
/// <para>
/// 回归的故障:终端把 <c>SupportsPreedit</c> 报成 false,而 Avalonia 的 Win32 后端从不让输入法
/// 自己画组字窗(<c>Imm32InputMethod.ShowCompositionWindow</c> 恒为 false)—— 两头都不画,
/// 屏幕上就只剩一个悬空的候选窗。用户看不到自己敲了什么,选错字要退几下只能靠猜。
/// </para>
/// <para>
/// 这条只有真读像素才验得到:逻辑层看着一切正常(组字串收到了、列也算对了),
/// 而叠加层是否真的把它画出来、有没有被光标或裁剪吃掉,只在屏幕上表现出来。
/// </para>
/// </summary>
[TestClass]
[TestCategory("GlyphRendering")]
public class ImePreeditRenderTests
{
    private static HeadlessUnitTestSession Session => SkiaTestSession.Current;

    private static void OnUi(Action body) =>
        Session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void Composing_PaintsThePinyinOnScreen()
    {
        OnUi(() =>
        {
            var control = new VelaTerminalControl
            {
                CopyOnSelect = false, // headless 下不去碰剪贴板
                ShowLineNumber = false,
                ShowLineTimestamp = false,
                ShowFoldMarker = false,
                CursorBlink = false,
            };
            control.Feed(Encoding.ASCII.GetBytes("$ "));

            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            control.Focus();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame(); // 填充屏幕行映射与单元格度量

            TextInputMethodClient client =
                control.ImeClientForTest
                ?? throw new AssertFailedException("终端没有提供 IME 客户端。");

            uint[] before = RenderFrame(window);
            client.SetPreeditText("ni'hao", 6);
            uint[] composing = RenderFrame(window);
            client.SetPreeditText(null, null);
            uint[] after = RenderFrame(window);
            window.Close();

            Assert.IsGreaterThan(
                0,
                Changed(before, composing),
                "组字期间屏幕一个像素都没变 —— 拼音没有画出来,这正是原故障的样子。");

            // 组字结束后必须回到原样:叠加层是纯视觉的,不该在屏幕上留下任何残迹
            // (它一个字节也没进屏幕缓冲)。
            Assert.AreEqual(
                0,
                Changed(before, after),
                "组字撤销后屏幕没有复原 —— 未提交的文本在屏幕上留了痕。");
        });
    }

    /// <summary>两帧之间颜色不同的像素数。</summary>
    private static int Changed(uint[] a, uint[] b)
    {
        Assert.HasCount(a.Length, b);
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                count++;
            }
        }
        return count;
    }

    private static uint[] RenderFrame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap bitmap = window.CaptureRenderedFrame()
            ?? throw new AssertFailedException(
                "没有拿到渲染帧。若 Skia 后端未生效(UseHeadlessDrawing 仍为 true),这里恒为 null。");

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int bytesPerPixel = 4;
        int bufferSize = checked(width * height * bytesPerPixel);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * bytesPerPixel);
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (uint)Marshal.ReadInt32(buffer, i * bytesPerPixel);
            }
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
