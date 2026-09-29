namespace Trenal.Core;

/// <summary>A VT100/xterm-compatible output surface (xterm.js on iOS, a raw tty on desktop).</summary>
public interface ITerminal
{
    /// <summary>Writes raw VT output. Called from the engine thread; must be thread-safe.</summary>
    void Write(string text);

    int Columns { get; }
    int Rows { get; }

    /// <summary>OSC window title, surfaced as the scene title on iPad.</summary>
    void SetTitle(string title) => Write($"\x1b]0;{title}\x07");
}
