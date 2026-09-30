using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Management.Automation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Trenal.Core;

/// <summary>P/Invoke surface of native/trenal-wasm/shim.c (WAMR interpreter + WASI).</summary>
static class WasmNative
{
    const string Lib = "trenal-wasm";

    [DllImport(Lib)] internal static extern int trenal_wasm_init();

    [DllImport(Lib)]
    internal static extern IntPtr trenal_wasm_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] err, int errlen);

    // String arrays can't be marshalled as UTF-8 automatically: callers pass Utf8Array pointers.
    [DllImport(Lib)]
    internal static extern int trenal_wasm_run(IntPtr w, IntPtr[] argv, int argc, IntPtr[] env, int envc, IntPtr[] maps, int nmaps,
        int inFd, int outFd, int errFd, uint stackSize, byte[] err, int errlen);

    [DllImport(Lib)] internal static extern void trenal_wasm_terminate(IntPtr w);

    /// <summary>NUL-terminated UTF-8 copies of strings, freed on dispose.</summary>
    internal sealed class Utf8Array(string[] items) : IDisposable
    {
        public IntPtr[] Pointers { get; } = items.Select(Marshal.StringToCoTaskMemUTF8).ToArray();

        public void Dispose()
        {
            foreach (var p in Pointers) Marshal.FreeCoTaskMem(p);
        }
    }

    internal static string Message(byte[] err) => Encoding.UTF8.GetString(err, 0, Math.Max(0, Array.IndexOf(err, (byte)0))).Trim();
}

/// <summary>A parsed module, kept for the app's lifetime: parsing python.wasm takes a while.</summary>
sealed class WasmModule
{
    static readonly ConcurrentDictionary<string, (DateTime Stamp, WasmModule Module)> Cache = new();

    public IntPtr Handle { get; }
    public object RunLock { get; } = new(); // WASI args live on the module: one run at a time

    WasmModule(IntPtr handle) => Handle = handle;

    public static WasmModule Load(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Module;
        if (WasmNative.trenal_wasm_init() == 0) throw new InvalidOperationException("WebAssembly runtime failed to initialise");
        var err = new byte[512];
        var handle = WasmNative.trenal_wasm_load(path, err, err.Length);
        if (handle == IntPtr.Zero) throw new InvalidOperationException($"{Path.GetFileName(path)}: {WasmNative.Message(err)}");
        var module = new WasmModule(handle);
        Cache[path] = (stamp, module); // old handles are leaked on purpose: a run may still use them
        return module;
    }
}

/// <summary>
/// Runs a WASI command-line program (.wasm) in-process: stdout/stderr go to the terminal (or
/// down the pipeline), stdin comes from pipeline input or the keyboard, the current directory
/// is pre-opened as "." and the filesystem root as "/". Ctrl+C terminates the program.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "Wasm")]
public sealed class InvokeWasmCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] public string Path { get; set; } = "";

    /// <summary>argv[0]; defaults to the file name without .wasm.</summary>
    [Parameter] public string? Name { get; set; }

    [Parameter(Position = 1, ValueFromRemainingArguments = true)] public string[] Arguments { get; set; } = [];

    [Parameter(ValueFromPipeline = true)] public PSObject? InputObject { get; set; }

    readonly List<string> input = [];
    volatile IntPtr runningModule;

    protected override void ProcessRecord()
    {
        if (InputObject is not null) input.Add(InputObject.ToString());
    }

    protected override void StopProcessing()
    {
        var m = runningModule;
        if (m != IntPtr.Zero) WasmNative.trenal_wasm_terminate(m);
    }

    protected override void EndProcessing()
    {
        var file = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Path);
        if (!File.Exists(file)) throw new PSArgumentException($"{Path}: no such .wasm file");
        var module = WasmModule.Load(file);
        var name = Name ?? System.IO.Path.GetFileNameWithoutExtension(file);
        var shell = HostServices.TryFrom(this)?.Shell;
        var term = shell?.Terminal;
        bool pipedIn = MyInvocation.ExpectingInput;
        bool captureOut = term is null || !OutputGoesToHost();

        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        var lines = new ConcurrentQueue<string>();
        var outDone = Pump(stdout, text => { if (captureOut) Split(text, lines); else term!.Write(Vt.Crlf(text)); }, captureOut ? () => Flush(lines) : null);
        var errDone = Pump(stderr, text => { if (term is not null) term.Write(Vt.Crlf(text)); else lines.Enqueue(text); }, null);

        var cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => $"{e.Key}={e.Value}").Append($"PWD={cwd}").ToArray();
        string[] argv = [name, .. Arguments];
        var stack = uint.TryParse(Environment.GetEnvironmentVariable("TRENAL_WASM_STACK_KB"), out var kb) ? kb * 1024 : 1024 * 1024;

        int exitCode = -1;
        string? error = null;
        var runner = new Thread(() =>
        {
            lock (module.RunLock)
            {
                runningModule = module.Handle;
                try
                {
                    exitCode = Run(module, argv, env, [$".::{cwd}", "/::/"], stdin, stdout, stderr, stack, out error);
                    // Some sandboxes (iOS) may refuse to pre-open "/"; retry with the current directory only.
                    if (exitCode == -1 && error?.Contains("pre-opening", StringComparison.Ordinal) == true)
                        exitCode = Run(module, argv, env, [$".::{cwd}"], stdin, stdout, stderr, stack, out error);
                }
                catch (Exception e)
                {
                    exitCode = -1;
                    error = e.Message; // a background thread must not throw: it would take the app down
                }
                finally
                {
                    runningModule = IntPtr.Zero;
                    stdout.DisposeLocalCopyOfClientHandle(); // EOF for the output pumps
                    stderr.DisposeLocalCopyOfClientHandle();
                }
            }
        }, maxStackSize: 16 * 1024 * 1024) { IsBackground = true, Name = "trenal-wasm" };
        runner.Start();

        IDisposable? raw = null;
        if (pipedIn)
        {
            FeedAndClose(stdin, string.Concat(input.Select(l => l + "\n")));
        }
        else if (shell is not null && term is not null)
        {
            var tty = new LineDiscipline(term,
                send: s => Write(stdin, s),
                eof: () => Close(stdin),
                interrupt: () => { StopProcessing(); Close(stdin); });
            raw = shell.BeginRawInput(tty.Feed);
        }
        else
        {
            Close(stdin); // no keyboard: stdin is empty
        }

        try
        {
            while (!runner.Join(50)) Drain(lines);
            outDone.Wait();
            errDone.Wait();
            Drain(lines);
        }
        finally
        {
            raw?.Dispose();
        }

        if (exitCode == -1 && error is not null)
            WriteError(new ErrorRecord(new InvalidOperationException($"{name}: {error}"), "Trenal.Wasm", ErrorCategory.InvalidResult, file));
        SessionState.PSVariable.Set("global:LASTEXITCODE", exitCode);
    }

    /// <summary>
    /// Same rule PowerShell uses for native commands: stdout goes straight to the terminal only
    /// when the next command is the host's Out-Default. Piped, assigned or redirected output is
    /// captured as lines. Works through function wrappers, which share their caller's pipe.
    /// </summary>
    bool OutputGoesToHost()
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var pipe = CommandRuntime.GetType().GetProperty("OutputPipe", Any)?.GetValue(CommandRuntime);
            var next = pipe?.GetType().GetProperty("DownstreamCmdlet", Any)?.GetValue(pipe);
            var info = next?.GetType().GetProperty("CommandInfo", Any)?.GetValue(next);
            return info is CmdletInfo { Name: "Out-Default" };
        }
        catch (Exception e) when (e is AmbiguousMatchException or TargetInvocationException)
        {
            return MyInvocation.PipelinePosition == MyInvocation.PipelineLength;
        }
    }

    static int Run(WasmModule module, string[] argv, string[] env, string[] maps,
        AnonymousPipeServerStream stdin, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr, uint stack, out string? error)
    {
        var err = new byte[512];
        using var a = new WasmNative.Utf8Array(argv);
        using var e = new WasmNative.Utf8Array(env);
        using var m = new WasmNative.Utf8Array(maps);
        int code = WasmNative.trenal_wasm_run(module.Handle, a.Pointers, argv.Length, e.Pointers, env.Length, m.Pointers, maps.Length,
            Fd(stdin), Fd(stdout), Fd(stderr), stack, err, err.Length);
        error = code == -1 ? WasmNative.Message(err) : null;
        return code;
    }

    static int Fd(AnonymousPipeServerStream s) => (int)s.ClientSafePipeHandle.DangerousGetHandle();

    void Drain(ConcurrentQueue<string> lines)
    {
        while (lines.TryDequeue(out var line)) WriteObject(line);
    }

    // Complete lines go down the pipeline as they arrive; a trailing partial line waits for more.
    readonly StringBuilder partial = new();

    void Split(string text, ConcurrentQueue<string> lines)
    {
        partial.Append(text);
        var s = partial.ToString();
        int start = 0, nl;
        while ((nl = s.IndexOf('\n', start)) >= 0)
        {
            lines.Enqueue(s[start..nl].TrimEnd('\r'));
            start = nl + 1;
        }
        partial.Clear().Append(s, start, s.Length - start);
    }

    void Flush(ConcurrentQueue<string> lines)
    {
        if (partial.Length > 0) lines.Enqueue(partial.ToString());
        partial.Clear();
    }

    static Task Pump(Stream from, Action<string> onText, Action? onEnd) => Task.Run(() =>
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024 + 4];
        try
        {
            int n;
            while ((n = from.Read(bytes, 0, bytes.Length)) > 0)
            {
                int c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) onText(new string(chars, 0, c));
            }
        }
        catch (IOException) { }
        onEnd?.Invoke();
    });

    static void FeedAndClose(AnonymousPipeServerStream stdin, string text) => Task.Run(() =>
    {
        Write(stdin, text);
        Close(stdin);
    });

    static void Write(Stream stdin, string text)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(text);
            stdin.Write(b, 0, b.Length);
            stdin.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { } // program stopped reading
    }

    static void Close(Stream stdin)
    {
        try { stdin.Dispose(); } catch (IOException) { }
    }
}

/// <summary>
/// Cooked-mode input for programs reading stdin, like a tty's line discipline: echo, erase,
/// Enter sends the line, Ctrl+D is EOF, Ctrl+C interrupts. Escape sequences (arrows, paste
/// markers) are dropped.
/// </summary>
sealed class LineDiscipline(ITerminal term, Action<string> send, Action eof, Action interrupt)
{
    readonly StringBuilder line = new();
    int escape; // 0 none, 1 after ESC, 2 inside CSI/SS3

    public void Feed(string data)
    {
        var echo = new StringBuilder();
        foreach (var ch in data)
        {
            if (escape == 1) { escape = ch is '[' or 'O' ? 2 : 0; continue; }
            if (escape == 2) { if (ch is >= '@' and <= '~') escape = 0; continue; }
            switch (ch)
            {
                case '\x1b': escape = 1; break;
                case '\r' or '\n':
                    echo.Append("\r\n");
                    Out(echo);
                    send(line.Append('\n').ToString());
                    line.Clear();
                    break;
                case '\x7f' or '\b':
                    if (line.Length == 0) break;
                    var s = line.ToString();
                    int start = StringInfo.ParseCombiningCharacters(s)[^1];
                    int w = CellWidth.Of(s[start..]);
                    line.Length = start;
                    if (w > 0) echo.Append('\b', w).Append(' ', w).Append('\b', w);
                    break;
                case '\x15': // Ctrl+U
                    int width = CellWidth.Of(line.ToString());
                    echo.Append('\b', width).Append(' ', width).Append('\b', width);
                    line.Clear();
                    break;
                case '\x03':
                    echo.Append("^C\r\n");
                    Out(echo);
                    line.Clear();
                    interrupt();
                    return;
                case '\x04':
                    Out(echo);
                    if (line.Length == 0) eof();
                    else { send(line.ToString()); line.Clear(); }
                    break;
                default:
                    if (ch >= ' ') { line.Append(ch); echo.Append(ch); }
                    break;
            }
        }
        Out(echo);
    }

    void Out(StringBuilder echo)
    {
        if (echo.Length == 0) return;
        term.Write(echo.ToString());
        echo.Clear();
    }
}
