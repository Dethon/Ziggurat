using System.Globalization;
using System.Text;

namespace Domain.Tools.Sugary;

// RFC 4180 with a header row, numbers in invariant form: what pandas, DuckDB and a spreadsheet
// import all read without being told anything. The text is plain .NET text; whoever turns it into
// bytes writes UTF-8 without a byte-order mark (the standalone app's export, carried over as is).
public sealed class SugaryCsv(params string[] columns)
{
    private readonly StringBuilder _text = new StringBuilder().AppendJoin(',', columns).Append('\n');

    public SugaryCsv Row(params object?[] cells)
    {
        _text.AppendJoin(',', cells.Select(Cell)).Append('\n');
        return this;
    }

    public override string ToString() => _text.ToString();

    private static string Cell(object? value) => value switch
    {
        null => "",
        string text => Quote(Defuse(text)),
        bool flag => flag ? "true" : "false",
        double or int => ((IFormattable)value).ToString("0.##", CultureInfo.InvariantCulture),
        _ => Quote(Defuse(value.ToString() ?? ""))
    };

    // A spreadsheet runs a cell that starts like a formula, and these cells hold text from the
    // person, the model and a public product database. A leading apostrophe makes it plain text.
    private static string Defuse(string text) =>
        text.Length > 0 && IsFormulaLead(text[0]) ? "'" + text : text;

    // The import's half of the same rule: an export it reads wrote "'=x" for "=x". Lossy for text
    // that really did begin with an apostrophe and a formula character, which is the price of an
    // export that is safe to open.
    public static string Restore(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && IsFormulaLead(cell[1]) ? cell[1..] : cell;

    private static bool IsFormulaLead(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static string Quote(string text) =>
        text.AsSpan().IndexOfAny(",\"\n\r") < 0 ? text : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // The rows of an RFC 4180 document, header included: a quoted cell may hold commas, doubled
    // quotes and line breaks; a row ends at LF or CRLF; a trailing line break adds no row.
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var rowStarted = false;

        // A character-level state machine: whether a comma or a line break ends a cell depends on
        // every quote before it, which LINQ cannot carry.
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    rowStarted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rowStarted = true;
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\n' or '\r':
                    if (rowStarted || cell.Length > 0)
                    {
                        row.Add(cell.ToString());
                        rows.Add(row);
                    }

                    row = [];
                    cell.Clear();
                    rowStarted = false;
                    break;
                default:
                    cell.Append(c);
                    rowStarted = true;
                    break;
            }
        }

        if (rowStarted || cell.Length > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}