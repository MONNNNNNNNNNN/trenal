using System.Text;
using System.Text.Json;
using CoreFoundation;
using Foundation;
using Trenal.Core;
using WebKit;

namespace Trenal.iOS;

/// <summary>
/// ITerminal over xterm.js. Output from the engine thread is coalesced and flushed once per
/// main-queue turn; a full buffer blocks the writer so `1..1e7` can't exhaust memory.
/// </summary>
sealed class WebTerminal(WKWebView web, Action<string> setTitle) : ITerminal
{
    const int MaxPending = 1 << 20;

    readonly StringBuilder pending = new();
    readonly object gate = new();
    bool scheduled;

    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public void Write(string text)
    {
        lock (gate)
        {
            // Never block the main thread: it is the one that drains the buffer.
            while (pending.Length > MaxPending && !NSThread.IsMain) Monitor.Wait(gate);
            pending.Append(text);
            if (scheduled) return;
            scheduled = true;
        }
        DispatchQueue.MainQueue.DispatchAsync(Flush);
    }

    public void SetTitle(string title) => DispatchQueue.MainQueue.DispatchAsync(() => setTitle(title));

    void Flush()
    {
        string chunk;
        lock (gate)
        {
            chunk = pending.ToString();
            pending.Clear();
            scheduled = false;
            Monitor.PulseAll(gate);
        }
        web.EvaluateJavaScript($"trenal.write({JsonSerializer.Serialize(chunk)})", null!);
    }
}

/// <summary>Sends [Console]::WriteLine output to the most recently opened terminal.</summary>
static class ConsoleRouter
{
    public static ITerminal? Target { get; set; }

    public static TextWriter Out { get; } = TextWriter.Synchronized(new Writer());

    sealed class Writer : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Target?.Write(value == '\n' ? "\r\n" : value.ToString());
        public override void Write(string? value)
        {
            if (value is not null) Target?.Write(Vt.Crlf(value));
        }
    }
}
