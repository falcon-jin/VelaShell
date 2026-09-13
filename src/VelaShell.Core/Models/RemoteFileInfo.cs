namespace VelaShell.Core.Models;

/// <summary>描述远端文件或目录的元数据信息。</summary>
public class RemoteFileInfo
{
    /// <summary>获取文件或目录的名称(不含路径)。</summary>
    public required string Name { get; init; }

    /// <summary>获取文件或目录的完整远端路径。</summary>
    public required string FullPath { get; init; }

    /// <summary>获取文件大小(字节)。</summary>
    public required long Size { get; init; }

    /// <summary>获取权限字符串(如 rwxr-xr-x)。</summary>
    public required string Permissions { get; init; }

    /// <summary>获取一个值,指示该条目是否为目录。</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// 该路径本身是否为符号链接。为 true 时 <see cref="IsDirectory" />、大小与修改时间描述的是链接指向的对象
    /// (因此指向目录的链接可以直接进入);删除与复制据此只处理链接本身,不沿链接递归。
    /// </summary>
    public bool IsSymbolicLink { get; init; }

    /// <summary>链接的原始目标文本(可能是相对路径);非链接或后端给不出时为 null。</summary>
    public string? LinkTarget { get; init; }

    /// <summary>获取最后修改时间。</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>获取文件或目录的属主。</summary>
    public required string Owner { get; init; }

    /// <summary>获取文件或目录所属的用户组。</summary>
    public required string Group { get; init; }
}
