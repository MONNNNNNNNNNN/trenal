using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text.RegularExpressions;

namespace Trenal.Core;

public sealed class ShellOptions
{
    /// <summary>Environment applied before the runspace opens (HOME, PATH, PSModulePath, TERM...).</summary>
    public IDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    public string Banner { get; init; } = "";

    /// <summary>Plain-text command history; one line per entry.</summary>
    public string? HistoryFile { get; init; }

    /// <summary>On `exit`, start a fresh session instead of ending (an app has nowhere to exit to).</summary>
    public bool RestartOnExit { get; init; }

    /// <summary>Folder picker/bookmarks for Mount-Folder. Defaults to plain paths in ~/.local/share/trenal/mounts.json.</summary>
    public IFolderAccess? Folders { get; init; }
}

/// <summary>
/// One interactive PowerShell session bound to a terminal. All PowerShell work happens on a
/// dedicated engine thread; the UI thread only calls <see cref="Input"/>.
/// </summary>
public sealed class Shell
{
    readonly ShellOptions options;
    readonly KeyParser parser = new();
    readonly KeyQueue keys = new();
    readonly LineEditor editor;
    readonly object state = new();
    PowerShell? running;
    bool hostReading;
    Runspace? runspace;
    TrenalHost? host;

    public Shell(ITerminal terminal, ShellOptions options)
    {
        Terminal = terminal;
        this.options = options;
        editor = new LineEditor(terminal, keys);
    }

    public ITerminal Terminal { get; }

    /// <summary>Raised on the engine thread when the shell ends (only without RestartOnExit).</summary>
    public event Action<int>? Exited;

    public void Start()
    {
        // iOS gives secondary threads 512 KB of stack; PowerShell's parser and
        // interpreter recurse deeply, so ask for much more.
        var thread = new Thread(Loop, maxStackSize: 16 * 1024 * 1024) { IsBackground = true, Name = "trenal-engine" };
        thread.Start();
    }

    /// <summary>Raw input from the terminal (UI thread).</summary>
    public void Input(string data)
    {
        List<Key> parsed;
        lock (parser) parsed = parser.Feed(data);
        var batch = new List<Key>(parsed.Count);
        foreach (var key in parsed)
        {
            if (key is { Kind: KeyKind.Ctrl, Ctrl: 'c' } && TryInterrupt()) continue;
            batch.Add(key);
        }
        if (batch.Count > 0) keys.Add(batch);
    }

    bool TryInterrupt()
    {
        lock (state)
        {
            if (running is null) return false;
            try { running.BeginStop(null, null); } catch (ObjectDisposedException) { }
            if (hostReading) keys.Add([new Key(KeyKind.Interrupt)]);
            return true;
        }
    }

    internal bool KeyAvailable => keys.Available;
    internal void FlushInput() => keys.Clear();

    internal string ReadForHost(EditMode mode)
    {
        lock (state) hostReading = true;
        try
        {
            var r = editor.ReadLine("", mode);
            if (r.Status is ReadStatus.Interrupted or ReadStatus.Cancelled) throw new PipelineStoppedException();
            return r.Text;
        }
        finally
        {
            lock (state) hostReading = false;
        }
    }

    internal Key ReadKeyForHost()
    {
        lock (state) hostReading = true;
        try
        {
            var k = keys.Take();
            if (k.Kind == KeyKind.Interrupt) throw new PipelineStoppedException();
            return k;
        }
        finally
        {
            lock (state) hostReading = false;
        }
    }

    void Loop()
    {
        int code;
        while (true)
        {
            try
            {
                code = RunSession();
            }
            catch (Exception e)
            {
                Terminal.Write($"{Sgr.Red}trenal: session crashed: {Vt.Crlf(e.ToString())}{Sgr.Reset}\r\n");
                code = 1;
            }
            if (!options.RestartOnExit) break;
            Terminal.Write("\r\n\x1b[2m[session ended, press Enter for a new one]\x1b[0m\r\n");
            while (keys.Take().Kind != KeyKind.Enter) { }
        }
        Exited?.Invoke(code);
    }

    int RunSession()
    {
        // Offline by default: System.Management.Automation otherwise reports module loads to
        // Application Insights. Must be set before the telemetry type initialises.
        Environment.SetEnvironmentVariable("POWERSHELL_TELEMETRY_OPTOUT", "1");
        foreach (var (k, v) in options.Environment) Environment.SetEnvironmentVariable(k, v);

        var folders = options.Folders ?? new JsonFolderAccess(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "trenal", "mounts.json"));
        host = new TrenalHost(this, new HostServices(folders));
        var iss = InitialSessionState.CreateDefault2();
        foreach (var (name, type) in TrenalCmdlets) iss.Commands.Add(new SessionStateCmdletEntry(name, type, null));
        using var rs = RunspaceFactory.CreateRunspace(host, iss);
        rs.ThreadOptions = PSThreadOptions.UseCurrentThread;
        rs.Open();
        runspace = rs;
        RestoreMounts(folders);
        editor.Completer = (text, cursor) =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            return CommandCompletion.CompleteInput(text, cursor, null, ps);
        };
        LoadHistory();

        if (options.Banner.Length > 0) Terminal.Write(Vt.Crlf(options.Banner) + "\r\n");
        Terminal.SetTitle("PowerShell");
        Execute(StartupScript(), addToHistory: false);

        while (!host.ShouldExit)
        {
            var r = editor.ReadLine(Prompt(), EditMode.Command);
            if (r.Status == ReadStatus.Eof) break;
            if (r.Status != ReadStatus.Line) continue;

            var text = r.Text;
            while (IsIncomplete(text))
            {
                var more = editor.ReadLine(">> ", EditMode.Plain);
                if (more.Status != ReadStatus.Line) { text = ""; break; }
                text += "\n" + more.Text;
            }
            if (string.IsNullOrWhiteSpace(text)) continue;

            // The single-line editor can't redraw multi-line entries; they stay in Get-History only.
            if (!text.Contains('\n'))
            {
                editor.AddHistory(text);
                AppendHistory(text);
            }
            Execute(text, addToHistory: true);
        }
        return host.ExitCode;
    }

    static readonly (string, Type)[] TrenalCmdlets =
    [
        ("Mount-Folder", typeof(MountFolderCommand)),
        ("Dismount-Folder", typeof(DismountFolderCommand)),
        ("Get-MountedFolder", typeof(GetMountedFolderCommand)),
    ];

    void RestoreMounts(IFolderAccess folders)
    {
        foreach (var (name, root) in folders.Restore())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddCommand("New-PSDrive").AddParameter("Name", name).AddParameter("PSProvider", "FileSystem")
                    .AddParameter("Root", root).AddParameter("Scope", "Global").Invoke();
            }
            catch (RuntimeException) { }
        }
    }

    void Execute(string script, bool addToHistory)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script).AddCommand("Out-Default");
        ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
        lock (state) running = ps;
        try
        {
            ps.Invoke(null, new PSInvocationSettings { AddToHistory = addToHistory });
        }
        catch (PipelineStoppedException) { }
        catch (Exception e)
        {
            ReportError(e);
        }
        finally
        {
            lock (state) running = null;
        }
    }

    void ReportError(Exception e)
    {
        var record = (e as IContainsErrorRecord)?.ErrorRecord ?? new ErrorRecord(e, "Trenal.Unhandled", ErrorCategory.NotSpecified, null);
        var obj = new PSObject(record);
        obj.Properties.Add(new PSNoteProperty("writeErrorStream", true));
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Out-Default").Invoke(new[] { obj });
        }
        catch
        {
            Terminal.Write($"{Sgr.Red}{Vt.Crlf(e.Message)}{Sgr.Reset}\r\n");
        }
    }

    string Prompt()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            var text = string.Concat(ps.AddCommand("prompt").Invoke().Select(o => o?.ToString()));
            return text.Length > 0 ? text : "PS> ";
        }
        catch
        {
            return "PS> ";
        }
    }

    static bool IsIncomplete(string text)
    {
        Parser.ParseInput(text, out _, out var errors);
        return errors.Any(e => e.IncompleteInput);
    }

    static string StartupScript()
    {
        using var s = typeof(Shell).Assembly.GetManifestResourceStream("Trenal.startup.ps1")!;
        return new StreamReader(s).ReadToEnd();
    }

    void LoadHistory()
    {
        if (options.HistoryFile is null || !File.Exists(options.HistoryFile) || editor.History.Count > 0) return;
        foreach (var line in File.ReadLines(options.HistoryFile).TakeLast(2000)) editor.AddHistory(line);
    }

    // Same heuristic as PSReadLine: likely secrets stay in memory history but never reach the
    // history file, which lives in Documents (visible in the Files app, included in backups).
    static readonly Regex Sensitive = new("password|asplaintext|token|apikey|secret", RegexOptions.IgnoreCase);

    void AppendHistory(string line)
    {
        if (options.HistoryFile is null || Sensitive.IsMatch(line)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.HistoryFile)!);
            File.AppendAllText(options.HistoryFile, line + "\n");
        }
        catch (IOException) { }
    }
}
