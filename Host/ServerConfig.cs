using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteScreen.Shared;

namespace RemoteScreen.Host;

/// <summary>Konfigurace v ~/.remotescreen/config.json. Heslo se neukládá, jen odvozený klíč (PBKDF2).</summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Key { get; set; } = "";
    public int Iterations { get; set; } = AuthCrypto.DefaultIterations;
    /// <summary>Zda už aplikace při prvním spuštění nastavila spouštění po přihlášení.</summary>
    public bool LoginItemConfigured { get; set; }

    /// <summary>Složka s nastavením. Mac: ~/.remotescreen, Windows: %APPDATA%\RemoteScreen\host (nastaví aplikace).</summary>
    public static string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remotescreen");
    private static string FilePath => Path.Combine(Dir, "config.json");
    private static string CertPath => Path.Combine(Dir, "server.pfx");

    public bool HasPassword => !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Key);

    /// <summary>Předchozí název aplikace – nastavení (heslo, certifikát) se při prvním spuštění převezme.</summary>
    private static string OldDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".macremote");

    private static void MigrateFromMacRemote()
    {
        try
        {
            if (Directory.Exists(Dir) || !Directory.Exists(OldDir)) return;
            EnsureDir();
            foreach (var f in Directory.GetFiles(OldDir))
            {
                string dest = Path.Combine(Dir, Path.GetFileName(f));
                File.Copy(f, dest, overwrite: false);
                TryChmod600(dest);
            }
            // spouštění po přihlášení se musí zaregistrovat znovu pro novou aplikaci
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(FilePath));
                if (c != null)
                {
                    c.LoginItemConfigured = false;
                    c.Save();
                }
            }
            Log.Info("Nastavení (heslo a certifikát) převzato z MacRemote.");
        }
        catch (Exception ex)
        {
            Log.Warn("Převzetí nastavení z MacRemote selhalo: " + ex.Message);
        }
    }

    public static ServerConfig Load()
    {
        if (OperatingSystem.IsMacOS()) MigrateFromMacRemote();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(FilePath)) ?? new ServerConfig();
        }
        catch (Exception ex)
        {
            Log.Warn("Konfiguraci nelze načíst: " + ex.Message);
        }
        return new ServerConfig();
    }

    public void Save()
    {
        EnsureDir();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        TryChmod600(FilePath);
    }

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Salt = Convert.ToBase64String(salt);
        Iterations = AuthCrypto.DefaultIterations;
        Key = Convert.ToBase64String(AuthCrypto.DeriveKey(password, salt, Iterations));
    }

    /// <summary>Načte nebo vytvoří self-signed certifikát pro TLS.</summary>
    public static X509Certificate2 LoadOrCreateCertificate()
    {
        EnsureDir();
        if (!File.Exists(CertPath))
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN=RemoteScreen {Environment.MachineName}", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx, ""));
            TryChmod600(CertPath);
            Log.Info("Vytvořen nový TLS certifikát: " + CertPath);
        }
#pragma warning disable SYSLIB0057 // v .NET 9+ je náhradou X509CertificateLoader
        // Windows: klíč musí být uložený (UserKeySet), jinak SslStream jako server selže
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet
            : X509KeyStorageFlags.Exportable;
        return new X509Certificate2(CertPath, "", flags);
#pragma warning restore SYSLIB0057
    }

    private static void EnsureDir()
    {
        if (!Directory.Exists(Dir))
        {
            Directory.CreateDirectory(Dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryChmod600(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }
}
