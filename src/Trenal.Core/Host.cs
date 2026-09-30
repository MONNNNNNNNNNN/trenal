using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using System.Text;

namespace Trenal.Core;

sealed class TrenalHost(Shell shell, HostServices services) : PSHost
{
    readonly Guid id = Guid.NewGuid();
    readonly HostUI ui = new(shell);
    readonly PSObject privateData = new(services);

    // trenal cmdlets find their services here ($Host.PrivateData).
    public override PSObject PrivateData => privateData;

    public override string Name => "trenal";
    public override Version Version => typeof(TrenalHost).Assembly.GetName().Version ?? new Version(0, 1);
    public override Guid InstanceId => id;
    public override PSHostUserInterface UI => ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;

    public bool ShouldExit { get; set; }
    public int ExitCode { get; private set; }

    public override void SetShouldExit(int exitCode)
    {
        ShouldExit = true;
        ExitCode = exitCode;
    }

    public override void EnterNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported.");
    public override void ExitNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported.");
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

sealed class HostUI(Shell shell) : PSHostUserInterface, IHostUISupportsMultipleChoiceSelection
{
    readonly RawUI raw = new(shell);

    public override PSHostRawUserInterface RawUI => raw;
    public override bool SupportsVirtualTerminal => true;

    ITerminal Term => shell.Terminal;

    public override void Write(string value) => Term.Write(Vt.Crlf(value));
    public override void WriteLine(string value) => Term.Write(Vt.Crlf(value) + "\r\n");

    // Leave the default background alone so the terminal theme shows through.
    public override void Write(ConsoleColor fg, ConsoleColor bg, string value) =>
        Term.Write(Sgr.Fg(fg) + (bg == raw.BackgroundColor ? "" : Sgr.Bg(bg)) + Vt.Crlf(value) + Sgr.Reset);

    public override void WriteErrorLine(string value) =>
        Term.Write((value.Contains('\x1b') ? Vt.Crlf(value) : Sgr.Red + Vt.Crlf(value) + Sgr.Reset) + "\r\n");

    public override void WriteWarningLine(string message) => Tagged("\x1b[33m", "WARNING: ", message);
    public override void WriteVerboseLine(string message) => Tagged("\x1b[33m", "VERBOSE: ", message);
    public override void WriteDebugLine(string message) => Tagged("\x1b[33m", "DEBUG: ", message);
    public override void WriteInformation(InformationRecord record) { }

    // Progress bars would fight the line editor for the cursor; skip them.
    public override void WriteProgress(long sourceId, ProgressRecord record) { }

    void Tagged(string color, string tag, string message) =>
        Term.Write(color + tag + Vt.Crlf(message) + Sgr.Reset + "\r\n");

    public override string ReadLine() => shell.ReadForHost(EditMode.Plain);

    public override SecureString ReadLineAsSecureString()
    {
        var s = new SecureString();
        foreach (char c in shell.ReadForHost(EditMode.Secret)) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        WriteHeader(caption, message);
        var result = new Dictionary<string, PSObject>();
        foreach (var fd in descriptions)
        {
            var type = Type.GetType(fd.ParameterAssemblyFullName) ?? typeof(string);
            Write($"{fd.Name}: ");
            if (type == typeof(SecureString))
                result[fd.Name] = new PSObject(ReadLineAsSecureString());
            else if (type == typeof(PSCredential))
                result[fd.Name] = new PSObject(PromptForCredential("", "", "", ""));
            else
                result[fd.Name] = new PSObject(LanguagePrimitives.ConvertTo(ReadLine(), type, CultureInfo.InvariantCulture));
        }
        return result;
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        var picked = PromptForChoice(caption, message, choices, defaultChoice < 0 ? [] : [defaultChoice]);
        return picked.Count > 0 ? picked[0] : defaultChoice;
    }

    public Collection<int> PromptForChoice(string? caption, string? message, Collection<ChoiceDescription> choices, IEnumerable<int>? defaultChoices)
    {
        WriteHeader(caption, message);
        var hotkeys = choices.Select(c => Hotkey(c.Label)).ToList();
        var defaults = defaultChoices?.ToList() ?? [];
        var line = new StringBuilder();
        for (int i = 0; i < choices.Count; i++)
            line.Append($"[{hotkeys[i]}] {choices[i].Label.Replace("&", "")}  ");
        line.Append("[?] Help");
        if (defaults.Count > 0)
            line.Append($" (default is \"{string.Join(",", defaults.Select(d => hotkeys[d]))}\")");
        while (true)
        {
            Write(line + ": ");
            var answer = ReadLine().Trim();
            if (answer.Length == 0 && defaults.Count > 0) return [.. defaults];
            if (answer == "?")
            {
                for (int i = 0; i < choices.Count; i++) WriteLine($"{hotkeys[i]} - {choices[i].HelpMessage}");
                continue;
            }
            int idx = hotkeys.FindIndex(h => h.Equals(answer, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = choices.ToList().FindIndex(c => c.Label.Replace("&", "").Equals(answer, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return [idx];
        }
    }

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        WriteHeader(caption, message);
        if (string.IsNullOrEmpty(userName))
        {
            Write("User: ");
            userName = ReadLine();
        }
        Write($"Password for {userName}: ");
        return new PSCredential(userName, ReadLineAsSecureString());
    }

    void WriteHeader(string? caption, string? message)
    {
        if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
        if (!string.IsNullOrEmpty(message)) WriteLine(message);
    }

    static string Hotkey(string label)
    {
        int amp = label.IndexOf('&');
        return amp >= 0 && amp + 1 < label.Length ? label[amp + 1].ToString().ToUpperInvariant() : label[..1].ToUpperInvariant();
    }
}

sealed class RawUI(Shell shell) : PSHostRawUserInterface
{
    string title = "trenal";

    ITerminal Term => shell.Terminal;
    Size Window => new(Math.Max(Term.Columns, 1), Math.Max(Term.Rows, 1));

    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;

    // Out-Default formats tables to BufferSize.Width, so it must track the live terminal width.
    public override Size BufferSize { get => new(Window.Width, 9001); set { } }
    public override Size WindowSize { get => Window; set { } }
    public override Size MaxWindowSize => Window;
    public override Size MaxPhysicalWindowSize => Window;
    public override Coordinates WindowPosition { get => new(0, 0); set { } }
    public override int CursorSize { get; set; } = 25;

    public override Coordinates CursorPosition
    {
        get => new(0, 0);
        set => Term.Write($"\x1b[{value.Y + 1};{value.X + 1}H");
    }

    public override string WindowTitle
    {
        get => title;
        set
        {
            title = value;
            Term.SetTitle(value);
        }
    }

    public override bool KeyAvailable => shell.KeyAvailable;
    public override void FlushInputBuffer() => shell.FlushInput();

    public override KeyInfo ReadKey(ReadKeyOptions options)
    {
        var key = shell.ReadKeyForHost();
        (char ch, int vk) = key.Kind switch
        {
            KeyKind.Text or KeyKind.Paste => (key.Text.Length > 0 ? key.Text[0] : '\0', char.ToUpperInvariant(key.Text.FirstOrDefault())),
            KeyKind.Enter => ('\r', 0x0D),
            KeyKind.Backspace => ('\b', 0x08),
            KeyKind.Tab => ('\t', 0x09),
            KeyKind.Escape => ('\x1b', 0x1B),
            KeyKind.Left => ('\0', 0x25),
            KeyKind.Up => ('\0', 0x26),
            KeyKind.Right => ('\0', 0x27),
            KeyKind.Down => ('\0', 0x28),
            KeyKind.Ctrl => ((char)(key.Ctrl - 'a' + 1), char.ToUpperInvariant(key.Ctrl)),
            _ => ('\0', 0),
        };
        if ((options & ReadKeyOptions.NoEcho) == 0 && ch >= ' ') Term.Write(ch.ToString());
        var state = key.Kind == KeyKind.Ctrl ? ControlKeyStates.LeftCtrlPressed : 0;
        return new KeyInfo(vk, ch, state, keyDown: true);
    }

    public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
    {
        // Clear-Host passes an all -1 rectangle.
        if (rectangle is { Left: -1, Top: -1, Right: -1, Bottom: -1 }) Term.Write("\x1b[2J\x1b[3J\x1b[H");
        else throw new NotSupportedException("Direct buffer access is not supported.");
    }

    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) =>
        throw new NotSupportedException("Direct buffer access is not supported.");

    public override BufferCell[,] GetBufferContents(Rectangle rectangle) =>
        throw new NotSupportedException("Direct buffer access is not supported.");

    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) =>
        throw new NotSupportedException("Direct buffer access is not supported.");
}

static class Sgr
{
    public const string Reset = "\x1b[0m";
    public const string Red = "\x1b[91m";

    static readonly int[] Ansi = [0, 4, 2, 6, 1, 5, 3, 7]; // ConsoleColor order -> ANSI colour index

    public static string Fg(ConsoleColor c) => $"\x1b[{((int)c < 8 ? 30 : 90) + Ansi[(int)c % 8]}m";
    public static string Bg(ConsoleColor c) => $"\x1b[{((int)c < 8 ? 40 : 100) + Ansi[(int)c % 8]}m";
}
