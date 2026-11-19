using System.Globalization;
using System.Text;

namespace Verso.Application.Executions;

/// <summary>The outcome of <see cref="SqlGuard.Check"/>: <c>Parameters</c> are the distinct <c>@name</c> placeholders in order of first use.</summary>
public sealed record SqlCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters);

/// <summary>The outcome of <see cref="SqlGuard.CheckWrite"/>: as <see cref="SqlCheck"/>, plus <c>Verb</c> (<c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c>, upper-case) when the statement is accepted.</summary>
public sealed record SqlWriteCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters, string? Verb);

/// <summary>
/// First safety net for the Query database step (<see cref="SqlGuard.Check"/>, read mode): accepts exactly one statement that starts with SELECT or WITH and contains
/// no INTO, write, DDL, transaction, session or procedure keyword. And for the Write to database step (<see cref="SqlGuard.CheckWrite"/>, write mode): exactly one
/// INSERT, UPDATE or DELETE whose verb is its first word. Because SQL Server runs a second statement after a DML statement without any ';', write mode refuses at
/// the top level (outside parentheses) everything that could start one: every other write, DDL, transaction, session or procedure keyword; the read-only T-SQL
/// statement verbs (IF, PRINT, THROW, RETURN, GOTO, READTEXT, FETCH, BREAK, CONTINUE, GET, MOVE); a WITH that is not a table hint; any SELECT in an UPDATE or
/// DELETE, and in an INSERT any SELECT but its source and one after each UNION / INTERSECT / EXCEPT / MINUS; a query in parentheses that does not follow an
/// operator, a comma, a subquery keyword or a known function name; a ')' without its '('. It also refuses INTO except right after INSERT, SET except as the one SET
/// clause of an UPDATE, UPDATE STATISTICS, and an UPDATE or DELETE without a top-level WHERE. Both modes share one tokenizer (comments, string literals and quoted
/// identifiers are skipped; the raw text is not searched), so the dialect rules below apply to both exactly.
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
/// A safety net only, in both modes: it does not parse SQL, it refuses text it cannot vouch for, and it can still miss a form a database accepts. For reading, the
/// real protection is a read-only database login. For writing, it is the "Allow writes" switch an administrator sets on the connection and the login's own
/// permissions (only the tables and columns the workflow may change); a WHERE the guard counts can still be <c>WHERE 1 = 1</c>.
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

    // Write mode only, at the top level: T-SQL statement verbs that cannot change data (so read mode allows them) but start a second statement after the write without
    // ';' (IF ... SELECT ... WHERE, RETURN, PRINT ...). END is not listed: CASE ... END is ordinary, and END CONVERSATION is caught by CONVERSATION.
    private static readonly HashSet<string> ReadOnlyStatementVerbs = new(StringComparer.Ordinal)
    {
        "IF", "PRINT", "THROW", "RETURN", "GOTO", "READTEXT", "FETCH", "BREAK", "CONTINUE", "GET", "MOVE",
    };

    // Write mode only: the words after which a top-level "(SELECT" is a subquery and not a second statement. Keywords that expect an expression or a table next, and
    // function names (SQL Server reads any name followed by '(' as a call; these are listed so that "COALESCE ((SELECT ...), 0)" passes). Not listed on purpose: words
    // that end an expression (NULL, CURRENT_TIMESTAMP, USER, END, ASC ...) and plain names (columns, aliases, collations).
    private static readonly HashSet<string> SubqueryOpeners = new(StringComparer.Ordinal)
    {
        "IN", "EXISTS", "ANY", "ALL", "SOME", "FROM", "JOIN", "APPLY", "UNION", "INTERSECT", "EXCEPT", "MINUS", "DISTINCT", "AND", "OR", "NOT", "CASE", "WHEN",
        "THEN", "ELSE", "WHERE", "ON", "HAVING", "SELECT", "TOP", "VALUES", "LIKE", "BETWEEN",
        "COALESCE", "ISNULL", "IFNULL", "NVL", "NULLIF", "IIF", "CAST", "TRY_CAST", "CONVERT", "TRY_CONVERT", "ABS", "ROUND", "FLOOR", "CEILING", "MAX", "MIN",
        "SUM", "AVG", "COUNT", "CONCAT", "UPPER", "LOWER", "LEN", "LENGTH", "TRIM", "LTRIM", "RTRIM", "SUBSTRING", "REPLACE", "DATEADD", "DATEDIFF", "GREATEST", "LEAST",
    };

    private static bool OpensSubquery(char previous, string lastWord) =>
        previous switch
        {
            'w' => SubqueryOpeners.Contains(lastWord),
            '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or ',' or '!' or '&' or '|' or '^' or '~' => true,
            _ => false, // a value, a quoted name, ')', a label's ':', '.', or nothing before it
        };

    /// <summary>The bare words <see cref="Check"/> refuses (besides INTO), for tests and documentation.</summary>
    public static IReadOnlyCollection<string> ForbiddenWords => Forbidden;

    private const string NotWrite = "sql.not_write";
    private const string NotWriteMessage = "Only an INSERT, UPDATE or DELETE statement is allowed.";

    /// <summary>Read mode, for the Query database step: one SELECT (or WITH ... SELECT) statement.</summary>
    public static SqlCheck Check(string? sql)
    {
        var r = Scan(sql, write: false);
        return r.Code is null ? new SqlCheck(true, null, null, r.Parameters) : new SqlCheck(false, r.Code, r.Message, []);
    }

    /// <summary>
    /// Write mode, for the Write to database step: one INSERT, UPDATE or DELETE statement. The same tokenizer as <see cref="Check"/> (every dialect refusal applies
    /// unchanged), plus: the first word is the verb and may appear only there; every other forbidden word (including a second INSERT / UPDATE / DELETE, so SQL Server's
    /// statement chaining without ';' is refused) gives <c>sql.not_write</c>; <c>INTO</c> only directly after INSERT; an UPDATE needs exactly one <c>SET</c>, at the
    /// top level and before the WHERE, and no other statement may hold one; T-SQL <c>UPDATE STATISTICS</c> is refused; UPDATE and DELETE need a <c>WHERE</c> word at
    /// the top level (outside parentheses), else <c>sql.no_where</c>. Whatever could start a second statement at the top level (see the class summary) is
    /// <c>sql.not_write</c> too, with a message naming the reason.
    /// </summary>
    public static SqlWriteCheck CheckWrite(string? sql)
    {
        var r = Scan(sql, write: true);
        return r.Code is null ? new SqlWriteCheck(true, null, null, r.Parameters, r.Verb) : new SqlWriteCheck(false, r.Code, r.Message, [], null);
    }

    private static ScanResult Scan(string? sql, bool write)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Empty(write);
        if (sql.Length > MaxLength) return Fail("sql.too_long", $"The query is longer than {MaxLength} characters.");
        if (CheckCharacters(sql) is { } badCharacter) return badCharacter;

        var parameters = new List<string>();
        string? first = null;
        var semicolon = false;
        var i = 0;
        // write mode only: the statement verb, the number of tokens so far (comments and whitespace are not tokens), the parenthesis depth, a top-level WHERE, the SET clause
        string? verb = null;
        var tokens = 0;
        var depth = 0;
        var topLevelWhere = false;
        var setSeen = false;
        var topLevelSelects = 0;  // top-level SELECTs of an INSERT (its source, then one after each UNION / INTERSECT / EXCEPT / MINUS)
        var valuesSeen = false;   // a top-level VALUES / VALUE / DEFAULT: the INSERT has its rows, a top-level SELECT after it is a second statement
        var setOperator = -2;     // the token number of the last top-level set operator (or of the ALL / DISTINCT right after it)
        char previous = '\0', current = '\0';
        var lastWord = "";        // the last word read, upper-case (the previous token's text when previous is 'w')
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

            tokens++;
            // the kind of this token and of the one before it: 'v' a value or quoted name, 'w' a word, otherwise the punctuation character itself
            previous = current;
            current = c is '\'' or '"' or '[' or '`' or '@' || char.IsAsciiDigit(c) ? 'v' : char.IsAsciiLetter(c) || c == '_' ? 'w' : c;

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
                if (write)
                {
                    lastWord = word.ToUpperInvariant();
                    if (first is not { Length: > 0 })
                    {
                        // the leading verb: the only place an INSERT, UPDATE or DELETE may appear ("" = the text started with a value, not a word)
                        if (first is not null || word.ToUpperInvariant() is not ("INSERT" or "UPDATE" or "DELETE")) return Fail(NotWrite, NotWriteMessage);
                        first = word;
                        verb = word.ToUpperInvariant();
                        i = j;
                        continue;
                    }
                    if (tokens == 2 && verb == "UPDATE" && word.Equals("STATISTICS", StringComparison.OrdinalIgnoreCase))
                        return Fail(NotWrite, "UPDATE STATISTICS is not an UPDATE of rows and is not allowed (quote a table called statistics).");
                    if (word.Equals("INTO", StringComparison.OrdinalIgnoreCase))
                    {
                        // INSERT INTO t: the token right after the verb (comments between them are not tokens); every other INTO (SELECT ... INTO, OUTPUT ... INTO) is refused
                        if (verb != "INSERT" || tokens != 2) return Fail("sql.select_into", "INTO is only allowed right after INSERT.");
                        i = j;
                        continue;
                    }
                    if (word.Equals("SET", StringComparison.OrdinalIgnoreCase) && verb == "UPDATE" && !setSeen && depth == 0 && !topLevelWhere)
                    {
                        // the SET clause of the UPDATE; any other SET (a second one, one after the WHERE, one in parentheses, one in an INSERT or DELETE) is a SET statement
                        setSeen = true;
                        i = j;
                        continue;
                    }
                    if (depth == 0)
                    {
                        // SQL Server starts a second statement without ';' at a SELECT, a CTE or a statement verb: at the top level of the write they are refused, so the
                        // WHERE that the rule below counts is the write's own and nothing runs after the write.
                        var upper = word.ToUpperInvariant();
                        if (ReadOnlyStatementVerbs.Contains(upper))
                            return Fail(NotWrite, $"Only one statement is allowed: {upper} would start a second statement on SQL Server (put it in parentheses or quote a column with that name).");
                        if (upper == "WITH" && NextSignificant(sql, j) != '(')
                            return Fail(NotWrite, "Only one statement is allowed: a WITH here starts a second statement; only table hints such as WITH (ROWLOCK) are allowed.");
                        if (upper == "SELECT")
                        {
                            // UPDATE and DELETE use SELECT only inside parentheses; an INSERT takes one as its source, plus one after each set operator
                            var source = verb == "INSERT" && !valuesSeen && (topLevelSelects == 0 || tokens == setOperator + 1);
                            if (!source)
                                return Fail(NotWrite, "Only one statement is allowed: this SELECT would run as a second statement on SQL Server (a subquery goes in parentheses).");
                            topLevelSelects++;
                        }
                        else if (upper is "VALUES" or "VALUE" or "DEFAULT") valuesSeen = true;
                        else if (upper is "UNION" or "INTERSECT" or "EXCEPT" or "MINUS") setOperator = tokens;
                        else if (upper is "ALL" or "DISTINCT" && tokens == setOperator + 1) setOperator = tokens;
                        else if (upper == "WHERE") topLevelWhere = true;
                    }
                    if (Forbidden.Contains(word))
                        return Fail(NotWrite, $"Only one INSERT, UPDATE or DELETE statement is allowed: {word.ToUpperInvariant()} is a write, DDL, transaction, session or procedure keyword here (quote a column with that name).");
                    i = j;
                    continue;
                }
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
            if (c == '(')
            {
                // SQL Server starts a second statement at "(SELECT" / "(WITH" (also behind more '(') right after a value, a ')', a label "name:" or a word that
                // ends an expression (NULL, CURRENT_TIMESTAMP, USER, END, a collation name ...), all checked on LocalDB. So at the top level such a '(' must follow
                // an operator, a comma, a subquery keyword or a known function name (OpensSubquery).
                if (write && depth == 0 && !OpensSubquery(previous, lastWord) && StartsQuery(sql, i + 1))
                    return Fail(NotWrite, "Only one statement is allowed: a query in parentheses here would run as a second statement on SQL Server.");
                depth++;
            }
            else if (c == ')' && --depth < 0 && write)
                // the depth would go negative: a later SELECT or statement verb would no longer look "top level", so a ')' without its '(' is refused in write mode
                return Fail(NotWrite, "A closing parenthesis has no opening one.");
            first ??= "";
            if (first.Length == 0) return NotStart(write);
            i++;
        }

        if (first is null) return Empty(write);
        if (first.Length == 0) return NotStart(write);
        if (verb == "UPDATE" && !setSeen)
            return Fail(NotWrite, "An UPDATE needs a SET clause before its WHERE condition.");
        if (verb is "UPDATE" or "DELETE" && !topLevelWhere)
            return Fail("sql.no_where", "UPDATE and DELETE need a WHERE condition, so a statement can never change a whole table by accident.");
        return new ScanResult(null, null, parameters, verb);
    }

    private static ScanResult Empty(bool write) =>
        write ? Fail("sql.empty", "Type an INSERT, UPDATE or DELETE statement.") : Fail("sql.empty", "Type a SELECT query.");

    private static ScanResult NotStart(bool write) =>
        write ? Fail(NotWrite, NotWriteMessage) : Fail("sql.not_select", "The query must start with SELECT or WITH.");

    private sealed record ScanResult(string? Code, string? Message, IReadOnlyList<string> Parameters, string? Verb);

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
    private static ScanResult? CheckCharacters(string sql)
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

    /// <summary>True when the text from <paramref name="index"/> on, past whitespace, comments and more '(', starts with the word SELECT or WITH.</summary>
    private static bool StartsQuery(string s, int index)
    {
        var i = SkipInsignificant(s, index, skipParentheses: true);
        var j = i;
        while (j < s.Length && IsNameChar(s[j])) j++;
        var word = s.AsSpan(i, j - i);
        return word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || word.Equals("WITH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The next character from <paramref name="index"/> on that is not whitespace or inside a comment ('\0' at the end or in an unclosed comment).</summary>
    private static char NextSignificant(string s, int index) => Peek(s, SkipInsignificant(s, index, skipParentheses: false));

    private static int SkipInsignificant(string s, int index, bool skipParentheses)
    {
        var i = index;
        while (i < s.Length)
        {
            if (s[i] is ' ' or '\t' or '\n' or '\r' || (skipParentheses && s[i] == '(')) { i++; continue; }
            if (s[i] == '-' && Peek(s, i + 1) == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (s[i] == '/' && Peek(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return s.Length; // unclosed: the scan refuses it anyway
                i = end + 2;
                continue;
            }
            return i;
        }
        return s.Length;
    }

    // A name continues with letters, digits, _ and $ in all four databases ($ never first).
    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$';

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsSafeBracketChar(char c) => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or ',' or '+';

    private static ScanResult Fail(string code, string message) => new(code, message, [], null);
}
