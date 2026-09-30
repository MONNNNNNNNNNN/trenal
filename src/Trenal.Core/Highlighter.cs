using System.Management.Automation.Language;
using System.Text;

namespace Trenal.Core;

/// <summary>Colours a command line with PowerShell's own tokenizer (PSReadLine's default palette).</summary>
static class Highlighter
{
    const string Reset = "\x1b[0m";

    public static string Colorize(string text)
    {
        if (text.Length == 0) return text;
        Token[] tokens;
        try
        {
            Parser.ParseInput(text, out tokens, out _);
        }
        catch
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 64);
        int last = 0;
        foreach (var t in tokens)
        {
            int start = t.Extent.StartOffset, end = t.Extent.EndOffset;
            if (start < last || end > text.Length || end <= start) continue;
            var color = ColorOf(t);
            if (color is null) continue;
            sb.Append(text, last, start - last).Append(color).Append(text, start, end - start).Append(Reset);
            last = end;
        }
        return sb.Append(text, last, text.Length - last).ToString();
    }

    static string? ColorOf(Token t)
    {
        if ((t.TokenFlags & TokenFlags.CommandName) != 0) return "\x1b[93m";
        return t.Kind switch
        {
            TokenKind.Variable or TokenKind.SplattedVariable => "\x1b[92m",
            TokenKind.StringLiteral or TokenKind.StringExpandable
                or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable => "\x1b[36m",
            TokenKind.Parameter => "\x1b[90m",
            TokenKind.Number => "\x1b[97m",
            TokenKind.Comment => "\x1b[32m",
            _ when (t.TokenFlags & TokenFlags.Keyword) != 0 => "\x1b[92m",
            _ when (t.TokenFlags & (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator | TokenFlags.AssignmentOperator)) != 0 => "\x1b[90m",
            _ => null,
        };
    }
}
