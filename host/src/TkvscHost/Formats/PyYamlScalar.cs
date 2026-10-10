using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TkvscHost.Formats;

/// <summary>
/// Scalars as PyYAML's dumper writes them (block context, unicode allowed): plain when that reads back as the same
/// string, otherwise single-quoted, otherwise double-quoted. Used for the texts the Python bridge made with PyYAML.
/// </summary>
public static partial class PyYamlScalar
{
    // What PyYAML's resolver would take a plain scalar for, other than a string.
    [GeneratedRegex(@"^(?:yes|Yes|YES|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$")]
    private static partial Regex BoolPattern();

    [GeneratedRegex(@"^(?:~|null|Null|NULL|)$")]
    private static partial Regex NullPattern();

    [GeneratedRegex(@"^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();

    [GeneratedRegex(@"^(?:[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]|[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?(?:[Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9][0-9]?(?::[0-9][0-9])?))?)$")]
    private static partial Regex TimestampPattern();

    private static bool ResolvesToString(string s)
        => s is not ("<<" or "=")
           && !BoolPattern().IsMatch(s) && !NullPattern().IsMatch(s) && !IntPattern().IsMatch(s)
           && !FloatPattern().IsMatch(s) && !TimestampPattern().IsMatch(s);

    private static bool IsSpaceOrBreak(char c) => c is '\0' or ' ' or '\t' or '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    private static bool IsBreak(char c) => c is '\n' or '\u0085' or '\u2028' or '\u2029';

    public static string Write(string value)
    {
        bool plainOk = false, singleOk = true;
        bool multiline = false;

        if (value.Length > 0)
        {
            bool blockInd = false, flowInd = false, lineBreaks = false, special = false;
            bool leadingSpace = false, leadingBreak = false, trailingSpace = false, trailingBreak = false;
            bool breakSpace = false, spaceBreak = false, previousSpace = false, previousBreak = false;

            if (value.StartsWith("---", StringComparison.Ordinal) || value.StartsWith("...", StringComparison.Ordinal))
                blockInd = flowInd = true;

            bool precededByWhitespace = true;
            bool followedByWhitespace = value.Length == 1 || IsSpaceOrBreak(value[1]);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (i == 0)
                {
                    if ("#,[]{}&*!|>'\"%@`".Contains(ch)) flowInd = blockInd = true;
                    if (ch is '?' or ':')
                    {
                        flowInd = true;
                        if (followedByWhitespace) blockInd = true;
                    }
                    if (ch == '-' && followedByWhitespace) flowInd = blockInd = true;
                }
                else
                {
                    if (",?[]{}".Contains(ch)) flowInd = true;
                    if (ch == ':')
                    {
                        flowInd = true;
                        if (followedByWhitespace) blockInd = true;
                    }
                    if (ch == '#' && precededByWhitespace) flowInd = blockInd = true;
                }

                if (IsBreak(ch)) lineBreaks = true;
                bool printable = ch == '\n' || (ch >= ' ' && ch <= '~')
                    || ch == '\u0085' || (ch >= '\u00A0' && ch <= '\uD7FF') || (ch >= '\uE000' && ch <= '\uFFFD') || char.IsSurrogate(ch);
                if (!printable || ch == '\uFEFF') special = true;

                if (ch == ' ')
                {
                    if (i == 0) leadingSpace = true;
                    if (i == value.Length - 1) trailingSpace = true;
                    if (previousBreak) breakSpace = true;
                    previousSpace = true;
                    previousBreak = false;
                }
                else if (IsBreak(ch))
                {
                    if (i == 0) leadingBreak = true;
                    if (i == value.Length - 1) trailingBreak = true;
                    if (previousSpace) spaceBreak = true;
                    previousSpace = false;
                    previousBreak = true;
                }
                else
                {
                    previousSpace = previousBreak = false;
                }

                precededByWhitespace = IsSpaceOrBreak(ch);
                followedByWhitespace = i + 2 >= value.Length || IsSpaceOrBreak(value[i + 2]);
            }

            multiline = lineBreaks;
            bool blockPlain = true;
            if (leadingSpace || leadingBreak || trailingSpace || trailingBreak) blockPlain = false;
            if (breakSpace) { blockPlain = false; singleOk = false; }
            if (spaceBreak || special) { blockPlain = false; singleOk = false; }
            if (lineBreaks) blockPlain = false;
            if (blockInd) blockPlain = false;
            plainOk = blockPlain;
        }

        if (plainOk && ResolvesToString(value) && !multiline) return value;
        if (singleOk && !multiline) return "'" + value.Replace("'", "''") + "'";
        return Double(value);
    }

    private static string Double(string value)
    {
        StringBuilder sb = new("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\v': sb.Append("\\v"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case '\u001b': sb.Append("\\e"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\u0085': sb.Append("\\N"); break;
                case '\u00A0': sb.Append("\\_"); break;
                case '\u2028': sb.Append("\\L"); break;
                case '\u2029': sb.Append("\\P"); break;
                default:
                    if (c < ' ' || c == '\u007f' || c == '\uFEFF') sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A float as PyYAML dumps it.</summary>
    public static string Float(double value)
    {
        if (double.IsNaN(value)) return ".nan";
        if (double.IsPositiveInfinity(value)) return ".inf";
        if (double.IsNegativeInfinity(value)) return "-.inf";
        string text = PyFloat.Repr(value).ToLowerInvariant();
        if (!text.Contains('.') && text.Contains('e')) text = text.Replace("e", ".0e", StringComparison.Ordinal);
        return text;
    }
}
