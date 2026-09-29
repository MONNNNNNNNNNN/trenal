using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Trenal.Core;

var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.CurrentDirectory;
bool selftest = args.Contains("--selftest");
if (selftest) home = Directory.CreateTempSubdirectory("trenal-home-").FullName;

var options = new ShellOptions
{
    Environment = new Dictionary<string, string?>
    {
        ["HOME"] = home,
        ["TERM"] = "xterm-256color",
        ["PSModulePath"] = BuiltInModules(),
    },
    Banner = $"trenal (desktop harness) · PowerShell on {RuntimeInformation.FrameworkDescription}"
        + (Type.GetType("Mono.RuntimeStructs") is null ? "" : $" · Mono {Environment.GetEnvironmentVariable("MONO_ENV_OPTIONS")}"),
    HistoryFile = Path.Combine(home, ".local", "share", "trenal", "history.txt"),
};

return selftest ? SelfTest.Run(options) : Interactive(options);

// A self-contained RID-specific publish leaves the built-in modules under runtimes/; point at them.
static string BuiltInModules()
{
    var baseDir = AppContext.BaseDirectory;
    var direct = Path.Combine(baseDir, "Modules");
    if (Directory.Exists(direct)) return direct;
    var runtimes = Path.Combine(baseDir, "runtimes", "unix", "lib");
    return Directory.Exists(runtimes)
        ? Directory.GetDirectories(runtimes).Select(d => Path.Combine(d, "Modules")).FirstOrDefault(Directory.Exists) ?? ""
        : "";
}

static int Interactive(ShellOptions options)
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("trenal: needs a terminal (or use --selftest)");
        return 2;
    }
    Stty("raw -echo");
    var terminal = new ConsoleTerminal();
    var shell = new Shell(terminal, options);
    var done = new ManualResetEventSlim();
    int exitCode = 0;
    shell.Exited += code => { exitCode = code; done.Set(); };
    shell.Start();

    var reader = new Thread(() =>
    {
        using var stdin = Console.OpenStandardInput();
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        int n;
        while ((n = stdin.Read(bytes)) > 0)
        {
            int c = decoder.GetChars(bytes, 0, n, chars, 0);
            if (c > 0) shell.Input(new string(chars, 0, c));
        }
    }) { IsBackground = true };
    reader.Start();

    done.Wait();
    Stty("sane");
    return exitCode;
}

static void Stty(string mode)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"stty {mode} < /dev/tty"]) { UseShellExecute = false });
        p?.WaitForExit();
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"trenal: stty failed: {e.Message}");
    }
}

sealed class ConsoleTerminal : ITerminal
{
    readonly Stream stdout = Console.OpenStandardOutput();
    readonly object gate = new();

    public int Columns => Safe(() => Console.WindowWidth, 80);
    public int Rows => Safe(() => Console.WindowHeight, 24);

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (gate)
        {
            stdout.Write(bytes);
            stdout.Flush();
        }
    }

    static int Safe(Func<int> f, int fallback)
    {
        try { return f() is > 0 and var v ? v : fallback; }
        catch { return fallback; }
    }
}
