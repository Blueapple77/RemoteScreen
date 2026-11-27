using System.Text;
using RemoteScreen.Shared;

namespace RemoteScreen.Server
{
    internal static class Entry
    {
        /// <summary>
        /// Režimy:
        ///   (výchozí)        aplikace s ikonou v horní liště (RemoteScreen.app)
        ///   --console        server v Terminálu bez ikony (ladění)
        ///   --setup          jen nastavit heslo v Terminálu a skončit
        /// Další volby: --port N, --name JMÉNO, --set-password, --debug-keys
        /// Main je záměrně synchronní – AppKit musí běžet na hlavním vlákně.
        /// </summary>
        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.UseFile();
                Log.Error("Spuštění selhalo: " + ex);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("RemoteScreen server běží jen na macOS.");
                return 1;
            }

            bool console = false, setup = false, setPassword = false;
            var cfg = ServerConfig.Load();
            bool changed = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--console": console = true; break;
                    case "--setup": setup = true; break;
                    case "--set-password": setPassword = true; break;
                    case "--debug-keys": ServerOptions.DebugKeys = true; break;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                        cfg.Port = port; i++; changed = true;
                        break;
                    case "--name" when i + 1 < args.Length:
                        cfg.Name = args[++i]; changed = true;
                        break;
                    default:
                        // macOS při spuštění z Finderu může přidat např. -psn_… – ignorovat
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(cfg.Name))
            {
                var (_, computerName) = SystemActions.RunProcessAsync("/usr/sbin/scutil", new[] { "--get", "ComputerName" })
                    .GetAwaiter().GetResult();
                cfg.Name = string.IsNullOrWhiteSpace(computerName) ? Environment.MachineName : computerName.Trim();
                changed = true;
            }

            bool interactive = !Console.IsInputRedirected;
            if ((setup || setPassword || console) && interactive && (!cfg.HasPassword || setPassword))
            {
                Console.WriteLine("Nastavte heslo, kterým se bude klient přihlašovat (min. 6 znaků).");
                while (true)
                {
                    string p1 = ReadPassword("Heslo: ");
                    if (p1.Length < 6) { Log.Warn("Heslo je příliš krátké."); continue; }
                    string p2 = ReadPassword("Heslo znovu: ");
                    if (p1 != p2) { Log.Warn("Hesla se neshodují."); continue; }
                    cfg.SetPassword(p1);
                    break;
                }
                changed = true;
            }

            if (changed) cfg.Save();

            if (setup || (setPassword && !console))
            {
                var cert = ServerConfig.LoadOrCreateCertificate();
                Log.Ok("Nastavení uloženo. Otisk certifikátu:");
                Log.Info("  " + AuthCrypto.Fingerprint(cert.RawData));
                return 0;
            }

            var host = new ServerHost(new MacPlatform());
            return console ? RunConsole(host) : RunApp(host);
        }

        private static int RunApp(ServerHost host)
        {
            Log.UseFile();
            Log.Info("RemoteScreen se spouští (režim aplikace).");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Neošetřená chyba: " + e.ExceptionObject);
            return MenuBarApp.Run(host);
        }

        private static int RunConsole(ServerHost host)
        {
            Console.WriteLine("RemoteScreen server – konzolový režim (Ctrl+C ukončí)");
            Console.WriteLine();
            if (!host.Config.HasPassword && !ServerConfig.Load().HasPassword)
            {
                Log.Error("Není nastavené heslo. Spusťte s --setup.");
                return 2;
            }

            Permissions.RequestMissing();
            if (Permissions.ScreenRecording) Log.Ok("Oprávnění Nahrávání obrazovky: OK");
            if (Permissions.Accessibility) Log.Ok("Oprávnění Zpřístupnění: OK");

            host.Start();
            if (!host.Running) return 1;

            using var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();
            host.Stop();
            return 0;
        }

        private static string ReadPassword(string prompt)
        {
            Console.Write(prompt);
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }
    }
}
