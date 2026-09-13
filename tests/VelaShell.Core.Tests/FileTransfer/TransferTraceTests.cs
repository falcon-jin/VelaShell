using VelaShell.Core.FileTransfer.Diagnostics;

namespace VelaShell.Core.Tests.FileTransfer;

/// <summary>
/// 传输诊断日志默认关闭,且关闭时必须零开销:逐帧 / 逐子包的 <c>Log($"...")</c> 不能因为日志关着
/// 就白白拼一次字符串。插值处理器在日志关闭时让编译器跳过全部拼接 —— 连插值洞里的表达式都不求值。
/// </summary>
[TestClass]
public class TransferTraceTests
{
    [TestMethod]
    public void Log_WhenDisabled_DoesNotEvaluateInterpolationHoles()
    {
        if (TransferTrace.IsEnabled)
        {
            Assert.Inconclusive("本进程设置了 VELASHELL_TRANSFER_TRACE,无法验证关闭状态。");
        }

        int evaluated = 0;
        TransferTrace.Log($"frame {Touch(ref evaluated)} pos={Touch(ref evaluated)}");

        Assert.AreEqual(0, evaluated, "日志关闭时插值洞不应被求值(即没有拼接字符串)");
    }

    private static int Touch(ref int counter) => ++counter;
}
