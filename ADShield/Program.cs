using ADShield.Forms;
using ADShield.Core;
using ADShield.Models;
using System.Runtime.InteropServices;

namespace ADShield;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [STAThread]
    static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            try
            {
                bool isRedirected = Console.IsOutputRedirected || Console.IsErrorRedirected;
                if (!isRedirected)
                {
                    AttachConsole(-1);
                }

                try
                {
                    IntPtr stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                    if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
                    {
                        var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, false);
                        var fileStream = new System.IO.FileStream(safeHandle, System.IO.FileAccess.Write);
                        var stdout = new System.IO.StreamWriter(fileStream, System.Text.Encoding.UTF8) { AutoFlush = true };
                        Console.SetOut(stdout);
                    }

                    IntPtr stdErrHandle = GetStdHandle(STD_ERROR_HANDLE);
                    if (stdErrHandle != IntPtr.Zero && stdErrHandle != new IntPtr(-1))
                    {
                        var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdErrHandle, false);
                        var fileStream = new System.IO.FileStream(safeHandle, System.IO.FileAccess.Write);
                        var stderr = new System.IO.StreamWriter(fileStream, System.Text.Encoding.UTF8) { AutoFlush = true };
                        Console.SetError(stderr);
                    }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(@"C:\BackupUtility\bootstrap_error.log", $"Stream setup failed: {ex.Message}\n{ex.StackTrace}\n"); } catch {}
                    try
                    {
                        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                        Console.SetOut(stdout);
                        var stderr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                        Console.SetError(stderr);
                    }
                    catch { }
                }

                await RunCliAsync(args);
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(@"C:\BackupUtility\bootstrap_error.log", $"CLI Execution Failed: {ex.Message}\n{ex.StackTrace}\n"); } catch {}
                Console.WriteLine($"\n[ERROR] CLI Execution Failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                FreeConsole();
                Environment.Exit(1);
            }
            FreeConsole();
            Environment.Exit(0);
        }

        // GUI Mode
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(
                $"An unhandled error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "AD Shield — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex?.Message}\n\n{ex?.StackTrace}",
                "AD Shield — Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.Run(new MainForm());
    }

    private static async Task RunCliAsync(string[] args)
    {
        string action = args[0].ToLower();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 < args.Length)
            {
                string key = args[i].TrimStart('-');
                dict[key] = args[i + 1];
            }
        }

        var settings = AppConfig.ReadSettings();

        // Helper to retrieve parameters
        string GetParam(string key, string? defaultValue = null, bool required = false)
        {
            if (dict.TryGetValue(key, out var val)) return val;
            if (required) throw new ArgumentException($"Missing required parameter: --{key}");
            return defaultValue ?? "";
        }

        var progress = new Progress<string>(msg => Console.WriteLine(msg));

        switch (action)
        {
            case "backup":
            case "-b":
            case "--backup":
                {
                    string computer = GetParam("computer", required: true);
                    string type = GetParam("type", "Full");
                    string password = GetParam("password", required: true);
                    
                    if (dict.TryGetValue("container", out var con)) settings.VeraCryptContainer = con;
                    if (dict.TryGetValue("letter", out var let)) settings.MountLetter = let;

                    var orch = new BackupOrchestrator(settings);
                    await orch.RunAsync(computer, type, password, progress);
                    break;
                }
            case "mount":
            case "-m":
            case "--mount":
                {
                    string password = GetParam("password", required: true);
                    if (dict.TryGetValue("container", out var con)) settings.VeraCryptContainer = con;
                    if (dict.TryGetValue("letter", out var let)) settings.MountLetter = let;

                    VeraCryptManager.Mount(settings, password, progress);
                    break;
                }
            case "dismount":
            case "-d":
            case "--dismount":
                {
                    if (dict.TryGetValue("letter", out var let)) settings.MountLetter = let;

                    VeraCryptManager.Dismount(settings, progress);
                    break;
                }
            case "create":
            case "-cr":
            case "--create":
                {
                    string container = GetParam("container", required: true);
                    string password = GetParam("password", required: true);
                    string size = GetParam("size", "10G");

                    settings.VeraCryptContainer = container;
                    VeraCryptManager.CreateContainer(settings, password, size, progress);
                    break;
                }
            case "discover":
            case "-ds":
            case "--discover":
                {
                    string ou = GetParam("ou", settings.SearchOU);
                    string group = GetParam("group", settings.AdGroup);

                    Console.WriteLine($"Running Active Directory discovery (OU: '{ou}', Group: '{group}')...");
                    var computers = AdDiscovery.Discover(ou, group, pingCheck: true, progress);
                    Console.WriteLine("\n--- Discovered Computers ---");
                    foreach (var c in computers)
                    {
                        Console.WriteLine($"{c.ComputerName,-15} | OS: {c.OperatingSystem,-20} | Online: {c.OnlineDisplay,-5} | Ping: {c.PingMs}ms");
                    }
                    break;
                }
            case "test":
            case "-ts":
            case "--test":
                {
                    Console.WriteLine("Running Backup Logic Test Suite...");
                    await BackupLogicTest.RunAllTests(progress, CancellationToken.None);
                    break;
                }
            case "diagnostics":
            case "-dg":
            case "--diagnostics":
                {
                    Console.WriteLine("Running VHDX Self-Healing Diagnostics...");
                    await BackupSelfHealingTest.RunDiagnosticTest(progress, CancellationToken.None);
                    break;
                }
            default:
                PrintHelp();
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ADShield — Active Directory Agentless Backup Utility (CLI Mode)");
        Console.WriteLine("\nUsage:");
        Console.WriteLine("  ADShield.exe <action> [parameters]");
        Console.WriteLine("\nActions:");
        Console.WriteLine("  --backup, -b       Run backup sequence for a computer");
        Console.WriteLine("                     Parameters: --computer <name> --password <pass> [--type <Full|Incremental>] [--container <path>] [--letter <letter>]");
        Console.WriteLine("  --mount, -m        Mount the VeraCrypt container");
        Console.WriteLine("                     Parameters: --password <pass> [--container <path>] [--letter <letter>]");
        Console.WriteLine("  --dismount, -d     Dismount the VeraCrypt container");
        Console.WriteLine("                     Parameters: [--letter <letter>]");
        Console.WriteLine("  --create, -cr      Create a new VeraCrypt container");
        Console.WriteLine("                     Parameters: --container <path> --password <pass> [--size <size>]");
        Console.WriteLine("  --discover, -ds    Discover domain computers");
        Console.WriteLine("                     Parameters: [--ou <OU>] [--group <Group>]");
        Console.WriteLine("  --test, -ts        Run backup logic test suite");
        Console.WriteLine("  --diagnostics, -dg Run VHDX self-healing diagnostics");
    }
}
