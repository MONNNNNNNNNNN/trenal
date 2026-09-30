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

int cmd = Array.IndexOf(args, "--command");
if (cmd >= 0 && cmd + 1 < args.Length) return Command(options, args[cmd + 1]);
return selftest ? SelfTest.Run(options) : Interactive(options);

// Non-interactive: run one script in a full trenal session (modules, cmdlets, startup). For development.
static int Command(ShellOptions options, string script) => new Shell(new ConsoleTerminal(), options).RunScript(script);

// trenal's modules sit in <output>/Modules; PowerShell's built-in ones in runtimes/unix/lib/<tfm>/Modules
// for a RID-specific self-contained publish, or <output>/Modules otherwise.
static string BuiltInModules()
{
    var baseDir = AppContext.BaseDirectory;
    var dirs = new List<string> { Path.Combine(baseDir, "Modules") };
    var runtimes = Path.Combine(baseDir, "runtimes", "unix", "lib");
    if (Directory.Exists(runtimes))
        dirs.AddRange(Directory.GetDirectories(runtimes).Select(d => Path.Combine(d, "Modules")));
    return string.Join(Path.PathSeparator, dirs.Where(Directory.Exists));
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
