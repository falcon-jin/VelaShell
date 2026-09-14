namespace VelaShell.Services.FileTransfer;

/// <summary>
/// ZMODEM 下载保存目录的选择请求:交给视图层弹出原生文件夹选择框时用作上下文
/// (标题显示首个文件名、建议起始目录等)。
/// </summary>
/// <param name="SuggestedDirectory">建议的起始目录(已做 <c>~</c> 展开的默认下载目录)。</param>
/// <param name="FirstFileName">本会话首个待接收文件名,用于对话框标题;可为 <c>null</c>。</param>
/// <param name="FirstFileSize">首个文件的字节大小(若发送方提供);可为 <c>null</c>。</param>
public sealed record TransferFolderPromptRequest(
    string SuggestedDirectory,
    string? FirstFileName,
    long? FirstFileSize);
