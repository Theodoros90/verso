using System.Globalization;
using System.Text;

namespace Verso.Application.Executions;

/// <summary>The outcome of <see cref="SqlGuard.Check"/>: <c>Parameters</c> are the distinct <c>@name</c> placeholders in order of first use.</summary>
public sealed record SqlCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters);

/// <summary>
/// First safety net for the Query database step: accepts exactly one statement that starts with SELECT or WITH and contains no INTO, write, DDL,
/// transaction, session or procedure keyword. It tokenizes the text (comments, string literals and quoted identifiers are skipped), it does not search the raw text.
/// <para>
/// One text is checked for four databases (SQL Server, PostgreSQL, MySQL / MariaDB, Oracle) that do not tokenize SQL the same way. Wherever they could disagree
/// about where a comment, a string or a quoted name ends, the text is <b>refused</b> (<c>sql.unsupported_syntax</c>), because a quote that one database reads inside
/// a comment and another reads as the start of a string can hide a second statement from the guard. Refused for that reason: a <c>--</c> not followed by a space,
/// tab or line end; <c>#</c>; <c>/*!</c> and <c>/*M!</c>; a <c>/*</c> inside a block comment (nesting); a carriage return not followed by a line feed; control
/// characters; any non-ASCII character outside text values, double-quoted names and comments (and, inside them, anything but letters, marks and digits);
/// a backslash; <c>$</c> outside a name; <c>q'</c>, <c>U&amp;'</c> and other prefixed literals except <c>N'</c>, <c>B'</c>, <c>X'</c>, <c>E'</c>;
/// <c>[...]</c> and backtick names with anything but letters, digits, spaces and <c>_ . , +</c> (other databases do not read them as names) or with a doubled closer;
/// a number glued to a name; <c>@</c> right after a name; <c>:name</c>; <c>{</c> and <c>}</c>.
/// </para>
/// A safety net only: the connection's database login must itself be read-only.
/// </summary>
public static class SqlGuard
{
    public const int MaxLength = 4000;
    private const int MaxParameterLength = 64;

    private const string Unsupported = "sql.unsupported_syntax";

    // Write and DDL statements, procedure calls, and the verbs of statements that SQL Server runs WITHOUT a separating ';' ("SELECT 1 COMMIT" is two statements there),
    // plus the statement verbs of the other databases that change the transaction, the session or the server. A column or alias with one of these names must be quoted.
    // Deliberately not listed: verbs that cannot change anything (IF, RETURN, BREAK, CONTINUE, GOTO, PRINT, THROW, READTEXT, FETCH, GET, MOVE, END: END CONVERSATION and
    // GET / MOVE CONVERSATION are caught by CONVERSATION), and START / LOAD / RELEASE, which only start a statement after a ';' on PostgreSQL / MySQL (refused anyway)
    // and are common words (Oracle's CONNECT BY ... START WITH).
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        // writes, DDL, permissions, procedures
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL",
        // SQL Server statement verbs (no ';' needed between statements)
        "COMMIT", "ROLLBACK", "SAVE", "BEGIN", "WAITFOR", "KILL", "SHUTDOWN", "BACKUP", "RESTORE", "DBCC", "RECONFIGURE", "RECEIVE", "SEND", "CONVERSATION",
        "WRITETEXT", "UPDATETEXT", "DECLARE", "USE", "SET", "SETUSER", "REVERT", "CHECKPOINT", "DISABLE", "ENABLE", "BULK", "OPEN", "CLOSE", "DEALLOCATE",
        "WHILE", "RAISERROR", "ADD",
        // ad hoc access to other servers and files
        "OPENROWSET", "OPENDATASOURCE", "OPENQUERY",
        // other databases' transaction, session and maintenance verbs
        "LOCK", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER", "COPY", "HANDLER", "PREPARE", "DO", "SAVEPOINT", "FLUSH", "PURGE", "LISTEN", "NOTIFY", "UNLISTEN",
        "DISCARD", "RESET", "REFRESH",
    };

    /// <summary>The bare words <see cref="Check"/> refuses (besides INTO), for tests and documentation.</summary>
    public static IReadOnlyCollection<string> ForbiddenWords => Forbidden;

    public static SqlCheck Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Fail("sql.empty", "Type a SELECT query.");
        if (sql.Length > MaxLength) return Fail("sql.too_long", $"The query is longer than {MaxLength} characters.");
        if (CheckCharacters(sql) is { } badCharacter) return badCharacter;

        var parameters = new List<string>();
        string? first = null;
        var semicolon = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is ' ' or '\t' or '\n' or '\r') { i++; continue; } // a '\r' is always followed by '\n' (CheckCharacters)

            if (c > '\x7E')
                return Fail(Unsupported, "Outside text values, double-quoted names and comments only plain ASCII characters are allowed.");

            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                // SQL Server, PostgreSQL and Oracle always start a comment at "--", MySQL only when a space or control character follows: only the form all agree on is accepted.
                if (i + 2 < sql.Length && sql[i + 2] is not (' ' or '\t' or '\n' or '\r'))
                    return Fail(Unsupported, "Write a \"--\" comment with a space after it (\"-- note\"); \"--\" followed by anything else is read differently by the databases.");
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                if (Peek(sql, i + 2) == '!' || (Peek(sql, i + 2) is 'M' or 'm' && Peek(sql, i + 3) == '!'))
                    return Fail(Unsupported, "MySQL and MariaDB executable comments (/*! and /*M!) are not allowed.");
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return Fail("sql.unterminated", "A comment is not closed.");
                // PostgreSQL and SQL Server nest block comments, MySQL and Oracle do not: a "/*" inside a comment (even "/*/") is refused.
                if (sql.IndexOf("/*", i + 2, end + 1 - (i + 2), StringComparison.Ordinal) >= 0)
                    return Fail(Unsupported, "A comment inside a comment is not allowed.");
                i = end + 2;
                continue;
            }

            // Anything that is not whitespace or a comment after a ';' is a second statement.
            if (semicolon) return Fail("sql.multiple_statements", "Only one statement is allowed.");

            if (c == ';') { semicolon = true; i++; continue; }

            if (c is '\'' or '"')
            {
                var j = i + 1;
                var closed = false;
                while (j < sql.Length)
                {
                    if (sql[j] == '\\')
                        return Fail(Unsupported, "Backslashes are not allowed inside text values; pass the value as a parameter.");
                    if (sql[j] == c)
                    {
                        if (Peek(sql, j + 1) == c) { j += 2; continue; } // doubled quote is an escape everywhere
                        closed = true;
                        break;
                    }
                    j++;
                }
                if (!closed) return Fail("sql.unterminated", "A string or quoted name is not closed.");
                first ??= "";
                i = j + 1;
                continue;
            }

            if (c is '[' or '`')
            {
                // SQL Server's [name] and MySQL's `name` are names only there; PostgreSQL reads '[' as a subscript and '`' as an operator, so their content must
                // tokenize the same both ways: only letters, digits, spaces and _ . , + are allowed, and a doubled closer (an escape in one, two tokens in the other) is refused.
                var close = c == '[' ? ']' : '`';
                var j = i + 1;
                while (j < sql.Length && sql[j] != close)
                {
                    if (!IsSafeBracketChar(sql[j]))
                        return Fail(Unsupported, "A [name] or `name` may only contain letters, digits, spaces and _ . , +; use \"name\" for other names.");
                    j++;
                }
                if (j >= sql.Length) return Fail("sql.unterminated", "A string or quoted name is not closed.");
                if (Peek(sql, j + 1) == close) return Fail(Unsupported, "A doubled ]] or `` is not allowed; use \"name\" for this name.");
                first ??= "";
                i = j + 1;
                continue;
            }

            if (c == '@')
            {
                if (i > 0 && (IsNameChar(sql[i - 1]) || sql[i - 1] is '"' or ']' or '`'))
                    return Fail(Unsupported, "An @ right after a name (as in table@dblink) is not allowed.");
                if (Peek(sql, i + 1) == '@')
                {
                    i += 2; // system variable such as @@ROWCOUNT, not a parameter
                    while (i < sql.Length && IsAsciiWordChar(sql[i])) i++;
                    first ??= "";
                    continue;
                }
                var j = i + 1;
                while (j < sql.Length && IsAsciiWordChar(sql[j])) j++;
                var name = sql[(i + 1)..j];
                if (name.Length == 0 || char.IsAsciiDigit(name[0]) || name.Length > MaxParameterLength)
                    return Fail("sql.bad_parameter", "A parameter name must start with a letter or underscore, use letters, digits and underscores, and be at most 64 characters.");
                if (!parameters.Contains(name)) parameters.Add(name);
                first ??= "";
                i = j;
                continue;
            }

            switch (c)
            {
                case '$':
                    return Fail(Unsupported, "A $ is only allowed inside a name: PostgreSQL dollar quoting and $1 placeholders are not accepted.");
                case '#':
                    return Fail(Unsupported, "# is not allowed outside text values (MySQL reads it as the start of a comment).");
                case '\\':
                    return Fail(Unsupported, "A backslash is not allowed outside text values.");
                case '{' or '}':
                    return Fail(Unsupported, "Braces (ODBC escapes) are not allowed outside text values.");
                case ':' when IsAsciiWordChar(Peek(sql, i + 1)) && (i == 0 || sql[i - 1] != ':'):
                    return Fail(Unsupported, "A :name placeholder is not allowed; use @name.");
                case '&' when Peek(sql, i + 1) is '\'' or '"':
                    return Fail(Unsupported, "U&'...' literals are not allowed.");
            }

            if (char.IsAsciiDigit(c))
            {
                var j = ScanNumber(sql, i);
                if (j < sql.Length && (IsNameChar(sql[j]) || sql[j] is '\'' or '"' or '`'))
                    return Fail(Unsupported, "Put a space or an operator between a number and a following name or text value.");
                first ??= "";
                i = j;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var j = i;
                while (j < sql.Length && IsNameChar(sql[j])) j++;
                var word = sql[i..j];
                if (Peek(sql, j) == '\'' && word.ToUpperInvariant() is not ("N" or "B" or "X" or "E"))
                    return Fail(Unsupported, "A text value right after a name (such as q'[...]' or _utf8'...') is not allowed; only N'...', B'...', X'...' and E'...' are.");
                if (first is null)
                {
                    if (!word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !word.Equals("WITH", StringComparison.OrdinalIgnoreCase))
                        return Fail("sql.not_select", "The query must start with SELECT or WITH.");
                    first = word;
                }
                if (word.Equals("INTO", StringComparison.OrdinalIgnoreCase)) return Fail("sql.select_into", "SELECT ... INTO is not allowed.");
                if (Forbidden.Contains(word))
                    return Fail("sql.not_select", $"Only reading is allowed: {word.ToUpperInvariant()} is a write, DDL, transaction, session or procedure keyword (quote a column with that name).");
                i = j;
                continue;
            }

            // other punctuation and operators
            first ??= "";
            if (first.Length == 0) return Fail("sql.not_select", "The query must start with SELECT or WITH.");
            i++;
        }

        if (first is null) return Fail("sql.empty", "Type a SELECT query.");
        if (first.Length == 0) return Fail("sql.not_select", "The query must start with SELECT or WITH.");
        return new SqlCheck(true, null, null, parameters);
    }

    /// <summary>
    /// Rewrites the <c>@name</c> placeholders to <paramref name="prefix"/> + name (Oracle wants <c>:name</c>), skipping comments, string literals and quoted names
    /// with the same rules as <see cref="Check"/>; <c>@@system</c> variables are left alone. Meant for text that <see cref="Check"/> accepted: from the first unclosed quote or comment on, the text is copied unchanged.
    /// </summary>
    public static string RewriteParameters(string sql, string prefix) =>
        prefix == "@" ? sql : Rewrite(sql, name => prefix + name);

    /// <summary>
    /// Like <see cref="RewriteParameters"/> but renames the placeholders to <paramref name="prefix"/> + p1, p2, ... by order of first use (a name used twice keeps its number;
    /// names differing in case are different, as in <see cref="Check"/>). <paramref name="names"/> lists the original names, index n is <c>p(n+1)</c>. For Oracle, which limits bind names
    /// to 30 characters, forbids reserved words as bind names and ignores case.
    /// </summary>
    public static string RewriteParametersNumbered(string sql, string prefix, out IReadOnlyList<string> names)
    {
        var order = new List<string>();
        var rewritten = Rewrite(sql, name =>
        {
            var index = order.IndexOf(name);
            if (index < 0) { order.Add(name); index = order.Count - 1; }
            return prefix + "p" + (index + 1);
        });
        names = order;
        return rewritten;
    }

    private static string Rewrite(string sql, Func<string, string> replacement)
    {
        var result = new StringBuilder(sql.Length + 16);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            int end;
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                // accepted text only has "--" followed by a space, a tab or a line end: a comment in every database, up to the line feed
                end = i;
                while (end < sql.Length && sql[end] != '\n') end++;
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = close < 0 ? sql.Length : close + 2;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                var closer = c == '[' ? ']' : c;
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == closer)
                    {
                        if (Peek(sql, j + 1) == closer) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                end = Math.Min(j + 1, sql.Length);
            }
            else if (c == '@' && Peek(sql, i + 1) == '@')
            {
                end = i + 2;
                while (end < sql.Length && IsAsciiWordChar(sql[end])) end++;
            }
            else if (c == '@' && IsAsciiWordChar(Peek(sql, i + 1)))
            {
                var j = i + 1;
                while (j < sql.Length && IsAsciiWordChar(sql[j])) j++;
                result.Append(replacement(sql[(i + 1)..j]));
                i = j;
                continue;
            }
            else
            {
                end = i + 1;
            }

            result.Append(sql, i, end - i);
            i = end;
        }
        return result.ToString();
    }

    /// <summary>
    /// The characters allowed anywhere: printable ASCII, tab, line feed, carriage return + line feed (a lone '\r' ends a "--" comment in PostgreSQL but not in MySQL),
    /// and non-ASCII letters, marks and digits that do not normalise (NFKC) to ASCII punctuation (a full-width quote or a modifier-letter apostrophe can become a quote
    /// when a driver converts the text to the database's character set). Where non-ASCII is allowed at all is decided by <see cref="Check"/>.
    /// </summary>
    private static SqlCheck? CheckCharacters(string sql)
    {
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\r')
            {
                if (Peek(sql, i + 1) != '\n') return Fail(Unsupported, "A carriage return must be followed by a line feed.");
                continue;
            }
            if (c is '\n' or '\t' || (c >= ' ' && c <= '\x7E')) continue;
            if (c < ' ' || c == '\x7F') return Fail(Unsupported, "Control characters (including a null character) are not allowed.");
            if (!IsAllowedNonAscii(c)) return Fail(Unsupported, "Only letters and digits may be non-ASCII characters (no special spaces, invisible characters, typographic quotes or symbols).");
        }
        return null;
    }

    private static bool IsAllowedNonAscii(char c)
    {
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber:
                break;
            default:
                // modifier letters (U+02BC looks like an apostrophe), spaces, format and control characters, surrogates, punctuation and symbols (the yen and won
                // signs are the backslash in some code pages)
                return false;
        }
        var normalised = c.ToString().Normalize(NormalizationForm.FormKC);
        return normalised.All(n => n > '\x7F' || char.IsAsciiLetterOrDigit(n));
    }

    private static int ScanNumber(string s, int i)
    {
        var j = i;
        if (s[i] == '0' && Peek(s, i + 1) is 'x' or 'X')
        {
            j = i + 2;
            while (j < s.Length && char.IsAsciiHexDigit(s[j])) j++;
            return j;
        }
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        if (Peek(s, j) == '.')
        {
            j++;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        }
        if (Peek(s, j) is 'e' or 'E' && (char.IsAsciiDigit(Peek(s, j + 1)) || (Peek(s, j + 1) is '+' or '-' && char.IsAsciiDigit(Peek(s, j + 2)))))
        {
            j += 2;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        }
        return j;
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    // A name continues with letters, digits, _ and $ in all four databases ($ never first).
    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$';

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsSafeBracketChar(char c) => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or ',' or '+';

    private static SqlCheck Fail(string code, string message) => new(false, code, message, []);
}
