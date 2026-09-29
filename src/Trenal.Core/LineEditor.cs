using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace Trenal.Core;

public enum EditMode { Command, Plain, Secret }

public enum ReadStatus { Line, Cancelled, Interrupted, Eof }

public readonly record struct ReadResult(ReadStatus Status, string Text);

/// <summary>
/// Minimal readline for a VT terminal: cursor editing over grapheme clusters, history, and
/// PowerShell tab completion. PSReadLine can't be used — it drives System.Console, which iOS lacks.
/// </summary>
public sealed class LineEditor(ITerminal term, KeyQueue input)
{
    readonly ITerminal term = term;
    readonly KeyQueue input = input;
    readonly List<string> history = [];

    /// <summary>Returns completions for (input, cursor). Runs on the engine thread.</summary>
    public Func<string, int, CommandCompletion?>? Completer { get; set; }

    public IReadOnlyList<string> History => history;

    public void AddHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || (history.Count > 0 && history[^1] == line)) return;
        history.Add(line);
        if (history.Count > 2000) history.RemoveRange(0, history.Count - 2000);
    }

    public ReadResult ReadLine(string prompt, EditMode mode) => new Session(this, prompt, mode).Run();

    sealed class Session
    {
        readonly LineEditor ed;
        readonly EditMode mode;
        readonly StringBuilder buf = new();
        readonly string promptLine;
        readonly int promptWidth;
        int pos;
        int renderedRow;
        int historyIndex;
        string stash = "";
        CommandCompletion? completion;
        int completionIndex, completionStart, completionLength;

        public Session(LineEditor ed, string prompt, EditMode mode)
        {
            this.ed = ed;
            this.mode = mode;
            historyIndex = ed.history.Count;
            // Only the prompt's last line is redrawn on edits; earlier lines are printed once.
            int nl = prompt.LastIndexOf('\n');
            if (nl >= 0) ed.term.Write(Vt.Crlf(prompt[..(nl + 1)]));
            promptLine = nl >= 0 ? prompt[(nl + 1)..] : prompt;
            promptWidth = CellWidth.Visible(promptLine);
        }

        bool IsCommand => mode == EditMode.Command;

        public ReadResult Run()
        {
            Render();
            while (true)
            {
                var key = ed.input.Take();
                if (key.Kind is not (KeyKind.Tab or KeyKind.ShiftTab)) completion = null;
                switch (key.Kind)
                {
                    case KeyKind.Text: Insert(key.Text); break;
                    case KeyKind.Paste: Paste(key.Text); break;
                    case KeyKind.Enter: return Finish(ReadStatus.Line, "");
                    case KeyKind.Backspace: if (pos > 0) DeleteRange(Prev(pos), pos); break;
                    case KeyKind.Delete: if (pos < buf.Length) DeleteRange(pos, Next(pos)); break;
                    case KeyKind.Left: if (pos > 0) pos = Prev(pos); break;
                    case KeyKind.Right: if (pos < buf.Length) pos = Next(pos); break;
                    case KeyKind.Home: pos = 0; break;
                    case KeyKind.End: pos = buf.Length; break;
                    case KeyKind.WordLeft: pos = WordLeft(pos); break;
                    case KeyKind.WordRight: pos = WordRight(pos); break;
                    case KeyKind.DeleteWordBack: DeleteRange(WordLeft(pos), pos); break;
                    case KeyKind.Up when IsCommand: HistoryMove(-1); break;
                    case KeyKind.Down when IsCommand: HistoryMove(+1); break;
                    case KeyKind.Tab when IsCommand: Complete(back: false); break;
                    case KeyKind.ShiftTab when IsCommand: Complete(back: true); break;
                    case KeyKind.Escape when IsCommand: buf.Clear(); pos = 0; break;
                    case KeyKind.Interrupt: return Finish(ReadStatus.Interrupted, "^C");
                    case KeyKind.Ctrl:
                        switch (key.Ctrl)
                        {
                            case 'c': return Finish(ReadStatus.Cancelled, "^C");
                            case 'd' when buf.Length == 0: return Finish(ReadStatus.Eof, "");
                            case 'd': if (pos < buf.Length) DeleteRange(pos, Next(pos)); break;
                            case 'a': pos = 0; break;
                            case 'e': pos = buf.Length; break;
                            case 'b': if (pos > 0) pos = Prev(pos); break;
                            case 'f': if (pos < buf.Length) pos = Next(pos); break;
                            case 'k': DeleteRange(pos, buf.Length); break;
                            case 'u': DeleteRange(0, pos); break;
                            case 'w': DeleteRange(WordLeft(pos), pos); break;
                            case 'p' when IsCommand: HistoryMove(-1); break;
                            case 'n' when IsCommand: HistoryMove(+1); break;
                            case 'l':
                                ed.term.Write("\x1b[2J\x1b[3J\x1b[H");
                                renderedRow = 0;
                                break;
                        }
                        break;
                }
                Render();
            }
        }

        ReadResult Finish(ReadStatus status, string marker)
        {
            pos = buf.Length;
            Render();
            ed.term.Write(marker + "\r\n");
            return new ReadResult(status, status == ReadStatus.Line ? buf.ToString() : "");
        }

        void Insert(string text)
        {
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\t') clean.Append("    ");
                else if (c >= ' ' && c != '\x7f') clean.Append(c);
            }
            buf.Insert(pos, clean);
            pos += clean.Length;
        }

        // Multi-line paste behaves like typing each line and pressing Enter, so PowerShell's
        // own continuation (">> ") handles blocks that span lines.
        void Paste(string text)
        {
            var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            Insert(lines[0]);
            var rest = new List<Key>();
            for (int i = 1; i < lines.Length; i++)
            {
                rest.Add(new Key(KeyKind.Enter));
                if (lines[i].Length > 0) rest.Add(new Key(KeyKind.Text, lines[i]));
            }
            if (rest.Count > 0) ed.input.PushFront(rest);
        }

        void DeleteRange(int from, int to)
        {
            if (to <= from) return;
            buf.Remove(from, to - from);
            pos = from;
        }

        int Next(int i) => i + StringInfo.GetNextTextElementLength(buf.ToString(), i);

        int Prev(int i)
        {
            var s = buf.ToString();
            int p = 0;
            while (true)
            {
                int n = p + StringInfo.GetNextTextElementLength(s, p);
                if (n >= i) return p;
                p = n;
            }
        }

        int WordLeft(int i)
        {
            while (i > 0 && char.IsWhiteSpace(buf[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(buf[i - 1])) i--;
            return i;
        }

        int WordRight(int i)
        {
            while (i < buf.Length && char.IsWhiteSpace(buf[i])) i++;
            while (i < buf.Length && !char.IsWhiteSpace(buf[i])) i++;
            return i;
        }

        void HistoryMove(int delta)
        {
            int target = historyIndex + delta;
            if (target < 0 || target > ed.history.Count) return;
            if (historyIndex == ed.history.Count) stash = buf.ToString();
            historyIndex = target;
            buf.Clear().Append(target == ed.history.Count ? stash : ed.history[target]);
            pos = buf.Length;
        }

        void Complete(bool back)
        {
            if (completion is null)
            {
                CommandCompletion? c = null;
                try { c = ed.Completer?.Invoke(buf.ToString(), pos); }
                catch { /* completion must never kill the prompt */ }
                if (c is null || c.CompletionMatches.Count == 0) return;
                completion = c;
                completionIndex = back ? c.CompletionMatches.Count - 1 : 0;
                completionStart = c.ReplacementIndex;
                completionLength = c.ReplacementLength;
                if (c.CompletionMatches.Count > 1) ShowCandidates(c);
            }
            else
            {
                int n = completion.CompletionMatches.Count;
                completionIndex = (completionIndex + (back ? n - 1 : 1)) % n;
            }
            var text = completion.CompletionMatches[completionIndex].CompletionText;
            buf.Remove(completionStart, completionLength).Insert(completionStart, text);
            completionLength = text.Length;
            pos = completionStart + text.Length;
        }

        // On a touch screen there's no menu; print the candidates once, then keep cycling with Tab.
        void ShowCandidates(CommandCompletion c)
        {
            const int max = 60;
            var items = c.CompletionMatches.Take(max).Select(m => m.ListItemText).ToList();
            int colWidth = Math.Min(items.Max(CellWidth.Of) + 2, 40);
            int perRow = Math.Max(1, ed.term.Columns / colWidth);
            var sb = new StringBuilder();
            MoveToEnd(sb);
            sb.Append("\r\n\x1b[2m");
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i].Length > colWidth - 2 ? items[i][..(colWidth - 3)] + "…" : items[i];
                sb.Append(item).Append(' ', Math.Max(1, colWidth - CellWidth.Of(item)));
                if ((i + 1) % perRow == 0 && i + 1 < items.Count) sb.Append("\r\n");
            }
            if (c.CompletionMatches.Count > max) sb.Append($"\r\n… {c.CompletionMatches.Count - max} more");
            sb.Append("\x1b[0m\r\n");
            ed.term.Write(sb.ToString());
            renderedRow = 0;
        }

        string Display(int upTo) => mode == EditMode.Secret
            ? new string('*', new StringInfo(buf.ToString(0, upTo)).LengthInTextElements)
            : buf.ToString(0, upTo);

        void MoveToEnd(StringBuilder sb)
        {
            int cols = Math.Max(1, ed.term.Columns);
            int endRow = (promptWidth + CellWidth.Of(Display(buf.Length))) / cols;
            if (endRow > renderedRow) sb.Append($"\x1b[{endRow - renderedRow}B");
        }

        void Render()
        {
            int cols = Math.Max(1, ed.term.Columns);
            var all = Display(buf.Length);
            var sb = new StringBuilder();
            if (renderedRow > 0) sb.Append($"\x1b[{renderedRow}A");
            sb.Append('\r').Append(promptLine).Append(all).Append("\x1b[J");

            int endWidth = promptWidth + CellWidth.Of(all);
            // Terminals defer the wrap at the last column; force it so row math stays exact.
            if (endWidth > 0 && endWidth % cols == 0) sb.Append("\r\n");
            int endRow = endWidth / cols;
            int cursorWidth = promptWidth + CellWidth.Of(Display(pos));
            int row = cursorWidth / cols, col = cursorWidth % cols;
            if (endRow > row) sb.Append($"\x1b[{endRow - row}A");
            sb.Append($"\x1b[{col + 1}G");
            renderedRow = row;
            ed.term.Write(sb.ToString());
        }
    }
}
