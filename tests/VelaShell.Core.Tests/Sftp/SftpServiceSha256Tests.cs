using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpServiceSha256Tests
{
    private const string Digest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly ISshConnectionService _connections = Substitute.For<ISshConnectionService>();
    private readonly ISshClientWrapper _ssh = Substitute.For<ISshClientWrapper>();
    private readonly Guid _session = Guid.NewGuid();

    [TestMethod]
    public async Task RunsOneExecForTheBatch_AndMapsTheOutput()
    {
        _connections.GetClient(_session).Returns(_ssh);
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteCommandResult($"{Digest}  /srv/a b.txt\n", "sha256sum: /srv/gone: No such file or directory\n", 1));
        var service = new SftpService(_connections, _ => Substitute.For<ISftpClientWrapper>());

        IReadOnlyDictionary<string, string?> result = await service.ComputeSha256Async(_session, ["/srv/a b.txt", "/srv/gone"]);

        Assert.AreEqual(Digest, result["/srv/a b.txt"]);
        Assert.IsNull(result["/srv/gone"]);
        await _ssh.Received(1).RunCommandDetailedAsync(
            Arg.Is<string>(c => c.Contains("sha256sum") && c.Contains("'/srv/a b.txt'") && c.Contains("'/srv/gone'")),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ExecRefused_IsNotSupported()
    {
        // 纯 SFTP 账号(ForceCommand internal-sftp)开 exec 通道会被拒。
        _connections.GetClient(_session).Returns(_ssh);
        _ssh.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("channel request refused"));
        var service = new SftpService(_connections, _ => Substitute.For<ISftpClientWrapper>());

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.ComputeSha256Async(_session, ["/a"]));
    }

    [TestMethod]
    public async Task NoSshClientForTheSession_IsNotSupported()
    {
        // 必须显式给 null:NSubstitute 对接口返回值默认给一个自动替身,不写这句测的就不是「没有客户端」。
        _connections.GetClient(_session).Returns((ISshClientWrapper?)null);
        var service = new SftpService(_connections, _ => Substitute.For<ISftpClientWrapper>());

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => service.ComputeSha256Async(_session, ["/a"]));
    }
}
