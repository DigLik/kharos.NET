using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace Kharos.Server.Tests;

/// <summary>tls.mode = files: wss с сертификатом из PEM и подмена сертификата без перезапуска порта.</summary>
public class TlsTests
{
    /// <summary>Самоподписанный сертификат для localhost: файлы PEM рядом.</summary>
    private static string Issue(Env.Temp temp, string name, string common)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={common}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(temp.File(name + ".crt"), cert.ExportCertificatePem());
        File.WriteAllText(temp.File(name + ".key"), key.ExportPkcs8PrivateKeyPem());
        return cert.Thumbprint;
    }

    private static void UseFiles(Env.Temp temp, JsonObject tree, string name)
    {
        var tls = (JsonObject)tree["tls"]!;
        tls["mode"] = "files";
        tls["cert"] = temp.File(name + ".crt");
        tls["key"] = temp.File(name + ".key");
    }

    [Fact]
    public async Task WssServesCertificateFromFilesAndReloadsItWithoutRestartingThePort()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        var first = Issue(temp, "one", "kharos-one");
        await using var server = await TestServer.StartAsync(temp, tree => UseFiles(temp, tree, "one"));
        Assert.True(server.Secure);
        var port = server.Port;

        string? seen = null;
        await using (var client = await TestClient.ConnectAsync(server, "a", certificate: cert =>
        {
            seen = cert?.Thumbprint;
            return true;
        }))
        {
            await client.WaitAsync(() => client.Replica.Ready, "wss: приветствие");
            Assert.Equal(first, seen);

            // Новый сертификат в тех же файлах: перечитывание подменяет его, открытое подключение живёт.
            var second = Issue(temp, "one", "kharos-two");
            Assert.NotEqual(first, second);
            Assert.True(server.Certificate!.Reload());
            Assert.False(server.Certificate.Reload());
            Assert.Equal(1, server.Clients);
            Assert.False(client.Finished);

            await using var next = await TestClient.ConnectAsync(server, "b", certificate: cert =>
            {
                seen = cert?.Thumbprint;
                return true;
            });
            await next.WaitAsync(() => next.Replica.Ready, "wss: второе подключение на том же порту");
            Assert.Equal(second, seen);
            Assert.Equal(port, server.Port);
        }
    }

    [Fact]
    public async Task BrokenFilesKeepOldCertificate()
    {
        using var temp = new Env.Temp();
        var thumbprint = Issue(temp, "one", "kharos-one");
        var log = new List<string>();
        using var certificate = new FileCertificate(temp.File("one.crt"), temp.File("one.key"), log.Add, Timeout.InfiniteTimeSpan);
        Assert.Equal(thumbprint, certificate.Current.Thumbprint);
        File.WriteAllText(temp.File("one.crt"), "мусор");
        Assert.False(certificate.Reload());
        Assert.Equal(thumbprint, certificate.Current.Thumbprint);
        Assert.Contains(log, line => line.StartsWith("сертификат не обновился"));
        File.Delete(temp.File("one.key"));
        Assert.False(certificate.Reload());
        Assert.Equal(thumbprint, certificate.Current.Thumbprint);
    }

    [Fact]
    public void FilesModeNeedsBothPaths()
    {
        var error = Assert.Throws<SettingsException>(() => new FileCertificate("", "", _ => { }));
        Assert.Contains("tls.cert", error.Message);
        using var temp = new Env.Temp();
        var missing = Assert.Throws<SettingsException>(() => new FileCertificate(temp.File("нет.crt"), temp.File("нет.key"), _ => { }));
        Assert.Contains("не читаются", missing.Message);
    }
}
