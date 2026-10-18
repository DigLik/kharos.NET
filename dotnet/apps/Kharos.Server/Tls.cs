using System.Security.Cryptography.X509Certificates;

namespace Kharos.Server;

/// <summary>
/// Сертификат режима <c>tls.mode: files</c>: PEM-файлы <c>cert</c> и <c>key</c>. Kestrel берёт сертификат при каждом
/// рукопожатии из <see cref="Current"/>, так что <see cref="Reload"/> меняет его без перезапуска порта: открытые
/// подключения не рвутся (в TS порт переоткрывался). Перечитывание — раз в <see cref="CheckInterval"/>.
/// </summary>
public sealed class FileCertificate : IDisposable
{
    /// <summary>Как часто проверяется, не сменились ли файлы (tls.ts: TLS_CHECK_INTERVAL).</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly string _certPath, _keyPath;
    private readonly Action<string> _log;
    private readonly Timer? _timer;
    private volatile X509Certificate2 _current;

    public FileCertificate(string cert, string key, Action<string> log, TimeSpan? interval = null)
    {
        if (cert.Length == 0 || key.Length == 0)
            throw new SettingsException("tls.mode = files: укажи tls.cert и tls.key");
        _certPath = cert;
        _keyPath = key;
        _log = log;
        _current = Read();
        var period = interval ?? CheckInterval;
        _timer = new Timer(_ => Reload(), null, period, period);
    }

    public X509Certificate2 Current => _current;

    /// <summary>Читает файлы заново; новый сертификат (другой отпечаток) подменяет старый. Не вышло — остаётся старый.</summary>
    public bool Reload()
    {
        try
        {
            var next = Read();
            if (next.Thumbprint == _current.Thumbprint)
            {
                next.Dispose();
                return false;
            }
            _current = next;
            _log($"сертификат обновлён, действует до {next.NotAfter:yyyy-MM-dd}");
            return true;
        }
        catch (SettingsException error)
        {
            _log($"сертификат не обновился: {error.Message}");
            return false;
        }
    }

    private X509Certificate2 Read()
    {
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(_certPath, _keyPath);
            // Windows (SChannel) не принимает ключ из PEM «эфемерным»: через PKCS#12 он становится обычным.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"сертификат {_certPath} и ключ {_keyPath} не читаются: {error.Message}");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
