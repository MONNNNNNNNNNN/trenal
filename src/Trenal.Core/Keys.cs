using System.Text;

namespace Trenal.Core;

public enum KeyKind
{
    Text, Enter, Backspace, Delete, Tab, ShiftTab, Escape,
    Left, Right, Up, Down, Home, End, WordLeft, WordRight, DeleteWordBack,
    Ctrl, Paste, Interrupt, Unknown,
}

/// <param name="Text">Typed/pasted text for <see cref="KeyKind.Text"/> and <see cref="KeyKind.Paste"/>.</param>
/// <param name="Ctrl">Lower-case letter for <see cref="KeyKind.Ctrl"/> (Ctrl+C is 'c').</param>
public readonly record struct Key(KeyKind Kind, string Text = "", char Ctrl = '\0');

/// <summary>
/// Turns the byte-ish stream xterm.js (or a raw tty) sends into keys. Keeps state across calls
/// because escape sequences and bracketed pastes can arrive split over several chunks.
/// </summary>
public sealed class KeyParser
{
    const string PasteStart = "\x1b[200~";
    const string PasteEnd = "\x1b[201~";
    string pending = "";

    public List<Key> Feed(string chunk)
    {
        var s = pending + chunk;
        pending = "";
        var keys = new List<Key>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\x1b')
            {
                int used = ParseEscape(s, i, keys);
                if (used == 0)
                {
                    pending = s[i..]; // incomplete sequence: wait for the rest
                    break;
                }
                i += used;
                continue;
            }
            switch (c)
            {
                case '\r':
                    keys.Add(new Key(KeyKind.Enter));
                    i += i + 1 < s.Length && s[i + 1] == '\n' ? 2 : 1;
                    continue;
                case '\n': keys.Add(new Key(KeyKind.Enter)); break;
                case '\x7f' or '\b': keys.Add(new Key(KeyKind.Backspace)); break;
                case '\t': keys.Add(new Key(KeyKind.Tab)); break;
                case < ' ': keys.Add(new Key(KeyKind.Ctrl, Ctrl: (char)(c + 'a' - 1))); break;
                default:
                    int start = i;
                    while (i < s.Length && s[i] >= ' ' && s[i] != '\x7f') i++;
                    keys.Add(new Key(KeyKind.Text, s[start..i]));
                    continue;
            }
            i++;
        }
        return keys;
    }

    /// <returns>Characters consumed, or 0 when the sequence is incomplete.</returns>
    static int ParseEscape(string s, int i, List<Key> keys)
    {
        int rest = s.Length - i;
        if (rest == 1)
        {
            // xterm.js delivers whole sequences per event, so a trailing lone ESC is the Esc key.
            keys.Add(new Key(KeyKind.Escape));
            return 1;
        }
        if (string.CompareOrdinal(s, i, PasteStart, 0, PasteStart.Length) == 0)
        {
            int end = s.IndexOf(PasteEnd, i + PasteStart.Length, StringComparison.Ordinal);
            if (end < 0) return 0;
            keys.Add(new Key(KeyKind.Paste, s[(i + PasteStart.Length)..end]));
            return end + PasteEnd.Length - i;
        }
        char n = s[i + 1];
        if (n == '[')
        {
            int j = i + 2;
            while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == ';')) j++;
            if (j >= s.Length) return 0;
            keys.Add(Csi(s[(i + 2)..j], s[j]));
            return j + 1 - i;
        }
        if (n == 'O')
        {
            if (rest < 3) return 0;
            keys.Add(Csi("", s[i + 2]));
            return 3;
        }
        // Meta/Option+key (macOptionIsMeta): readline-style word motions.
        keys.Add(n switch
        {
            'b' => new Key(KeyKind.WordLeft),
            'f' => new Key(KeyKind.WordRight),
            '\x7f' or '\b' => new Key(KeyKind.DeleteWordBack),
            _ => new Key(KeyKind.Unknown),
        });
        return 2;
    }

    static Key Csi(string args, char final)
    {
        // "1;3D" = Alt+Left, "1;5D" = Ctrl+Left
        bool word = args.EndsWith(";3") || args.EndsWith(";5") || args.EndsWith(";9");
        return final switch
        {
            'A' => new Key(KeyKind.Up),
            'B' => new Key(KeyKind.Down),
            'C' => new Key(word ? KeyKind.WordRight : KeyKind.Right),
            'D' => new Key(word ? KeyKind.WordLeft : KeyKind.Left),
            'H' => new Key(KeyKind.Home),
            'F' => new Key(KeyKind.End),
            'Z' => new Key(KeyKind.ShiftTab),
            '~' => args switch
            {
                "1" or "7" => new Key(KeyKind.Home),
                "4" or "8" => new Key(KeyKind.End),
                "3" => new Key(KeyKind.Delete),
                _ => new Key(KeyKind.Unknown),
            },
            _ => new Key(KeyKind.Unknown),
        };
    }
}

/// <summary>Terminal column width of text: 0 for combining marks (Thai vowels/tones), 2 for East Asian wide.</summary>
public static class CellWidth
{
    public static int Of(string s)
    {
        int w = 0;
        foreach (var r in s.EnumerateRunes()) w += Of(r);
        return w;
    }

    public static int Of(Rune r)
    {
        int v = r.Value;
        if (v < 0x20 || v == 0x7f) return 0;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.Format) return 0;
        return IsWide(v) ? 2 : 1;
    }

    static bool IsWide(int v) =>
        v is >= 0x1100 and <= 0x115F
        or >= 0x2E80 and <= 0x303E
        or >= 0x3041 and <= 0x33FF
        or >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xA000 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1F64F
        or >= 0x1F900 and <= 0x1F9FF
        or >= 0x20000 and <= 0x3FFFD;

    /// <summary>Visible width of a string that may contain SGR/OSC escape sequences (e.g. a coloured prompt).</summary>
    public static int Visible(string s) => Of(Vt.Strip(s));
}

public static class Vt
{
    // Not RegexOptions.Compiled: on iOS there is no JIT, so compiling only costs startup time.
    static readonly System.Text.RegularExpressions.Regex Escapes =
        new(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-_]");

    public static string Strip(string s) => s.Contains('\x1b') ? Escapes.Replace(s, "") : s;

    /// <summary>PowerShell emits bare \n; a raw terminal needs \r\n.</summary>
    public static string Crlf(string s) => s.Contains('\n') ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s;
}
