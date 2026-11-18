using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class SqlGuardTests
{
    [Fact]
    public void Accepts_plain_select()
    {
        var r = SqlGuard.Check("SELECT name FROM customers WHERE id = @id");
        Assert.True(r.Ok);
        Assert.Equal("id", Assert.Single(r.Parameters));
    }

    [Fact]
    public void Accepts_cte()
    {
        Assert.True(SqlGuard.Check("WITH x AS (SELECT 1 a) SELECT a FROM x").Ok);
    }

    [Fact]
    public void Rejects_empty_and_too_long()
    {
        Assert.Equal("sql.empty", SqlGuard.Check(null).Code);
        Assert.Equal("sql.empty", SqlGuard.Check("   ").Code);
        Assert.Equal("sql.too_long", SqlGuard.Check("SELECT '" + new string('a', SqlGuard.MaxLength) + "'").Code);
        Assert.True(SqlGuard.Check("SELECT '" + new string('a', SqlGuard.MaxLength - 10) + "'").Ok);
    }

    [Fact]
    public void Rejects_second_statement()
    {
        Assert.Equal("sql.multiple_statements", SqlGuard.Check("SELECT 1; DROP TABLE t").Code);
        Assert.True(SqlGuard.Check("SELECT 1;").Ok);
        Assert.True(SqlGuard.Check("SELECT 1;  \n ").Ok);
        Assert.True(SqlGuard.Check("SELECT 1; -- done").Ok);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("EXEC sp_who")]
    [InlineData("CALL p()")]
    [InlineData("MERGE t USING s ON 1=1")]
    [InlineData("DROP TABLE t")]
    public void Rejects_non_select_start(string sql)
    {
        Assert.Equal("sql.not_select", SqlGuard.Check(sql).Code);
    }

    [Fact]
    public void Rejects_select_into()
    {
        Assert.Equal("sql.select_into", SqlGuard.Check("SELECT a INTO t2 FROM t").Code);
        Assert.Equal("sql.select_into", SqlGuard.Check("select a into t2 from t").Code);
    }

    [Fact]
    public void Rejects_exec_or_call_anywhere()
    {
        Assert.Equal("sql.not_select", SqlGuard.Check("SELECT 1 FROM t WHERE x IN (EXEC p)").Code);
    }

    [Fact]
    public void Accepts_comments_that_hide_text()
    {
        Assert.True(SqlGuard.Check("SELECT 1 -- ; DROP").Ok);
        Assert.True(SqlGuard.Check("/* hi */ SELECT 1").Ok);
    }

    [Fact]
    public void Rejects_comment_hiding_text()
    {
        Assert.Equal("sql.multiple_statements", SqlGuard.Check("SELECT 1 /* x */; DELETE FROM t").Code);
        Assert.Equal("sql.not_select", SqlGuard.Check("/* SELECT */ DELETE FROM t").Code);
    }

    [Fact]
    public void Ignores_keywords_inside_string_literals_and_identifiers()
    {
        Assert.True(SqlGuard.Check("SELECT 'drop; into' AS note").Ok);
        Assert.True(SqlGuard.Check("SELECT updated_at FROM t").Ok);
        Assert.True(SqlGuard.Check("SELECT \"into\", [exec], `call` FROM t").Ok);
        Assert.True(SqlGuard.Check("SELECT 'it''s; into' FROM t").Ok);
        Assert.Empty(SqlGuard.Check("SELECT '@x' FROM t").Parameters);
    }

    [Fact]
    public void Rejects_unterminated_string_or_comment()
    {
        Assert.Equal("sql.unterminated", SqlGuard.Check("SELECT 'abc").Code);
        Assert.Equal("sql.unterminated", SqlGuard.Check("SELECT 1 /* abc").Code);
        Assert.Equal("sql.unterminated", SqlGuard.Check("SELECT \"abc").Code);
        Assert.Equal("sql.unterminated", SqlGuard.Check("SELECT [abc").Code);
    }

    [Fact]
    public void Collects_distinct_parameters_case_sensitively()
    {
        var r = SqlGuard.Check("SELECT 1 FROM t WHERE a = @b AND c = @B AND d = @b AND e = @a1 AND f = @@ROWCOUNT");
        Assert.True(r.Ok);
        Assert.Equal("b,B,a1", string.Join(",", r.Parameters));
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t")]
    [InlineData("WITH x AS (SELECT 1) UPDATE t SET a = 1")]
    [InlineData("WITH x AS (SELECT 1 a) INSERT INTO t SELECT a FROM x")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("SELECT 1 FROM t; ALTER TABLE t ADD c int")]
    [InlineData("WITH x AS (SELECT 1) CREATE TABLE y (a int)")]
    public void Rejects_cte_followed_by_write(string sql)
    {
        Assert.False(SqlGuard.Check(sql).Ok);
    }

    [Fact]
    public void Write_keywords_only_count_as_bare_words()
    {
        Assert.True(SqlGuard.Check("SELECT updated_at, \"delete\", 'update' FROM t").Ok);
    }

    [Theory]
    [InlineData("SELECT $$'$$; DROP TABLE t; --'")]
    [InlineData("SELECT $tag$x$tag$")]
    public void Rejects_dollar_quoting(string sql)
    {
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check(sql).Code);
    }

    [Fact]
    public void Allows_dollar_inside_a_name_but_not_a_dollar_placeholder()
    {
        // Changed deliberately in the final fix wave: "$1" used to pass; a $ outside a name is now refused (PostgreSQL could read "1$$...$$" as a dollar quote).
        Assert.True(SqlGuard.Check("SELECT a$b FROM t WHERE x = 1").Ok);
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check("SELECT a$b FROM t WHERE x = $1").Code);
    }

    [Fact]
    public void Rejects_double_dash_without_space_hiding_statement()
    {
        // Changed deliberately in the final fix wave: "--" without a space used to be read MySQL's way (two minus signs) and only the ';' made these fail;
        // SQL Server, PostgreSQL and Oracle read it as a comment, so it is now refused on its own.
        Assert.False(SqlGuard.Check("SELECT 5--1; DELETE FROM t").Ok);
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check("SELECT 5--1; SELECT 2").Code);
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check("SELECT 5--1").Code);
        Assert.True(SqlGuard.Check("SELECT 5 - -1").Ok);
    }

    [Theory]
    [InlineData("SELECT 1 /*! DELETE FROM t */")]
    [InlineData("SELECT 1 /*!50000 , 2 */")]
    public void Rejects_mysql_executable_comment(string sql)
    {
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check(sql).Code);
    }

    [Fact]
    public void Accepts_double_dash_comment_with_space_at_end()
    {
        Assert.True(SqlGuard.Check("SELECT 1 -- note").Ok);
        Assert.True(SqlGuard.Check("SELECT 1 --").Ok);
        Assert.True(SqlGuard.Check("SELECT 1 --\nFROM t").Ok);
    }

    [Theory]
    [InlineData(@"SELECT 'a\'' ; DELETE FROM t ; --'")]
    [InlineData(@"SELECT 'a\\b'")]
    [InlineData(@"SELECT ""a\b""")]
    public void Rejects_backslash_escape_in_string(string sql)
    {
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check(sql).Code);
    }

    [Fact]
    public void Accepts_string_without_backslash()
    {
        Assert.True(SqlGuard.Check("SELECT 'a''b' FROM t WHERE p = 'x/y'").Ok);
        // Changed deliberately in the final fix wave: a backslash inside [name] used to pass; [name] may now only hold letters, digits, spaces and _ . , +
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check("SELECT [a\\b] FROM t").Code);
        Assert.True(SqlGuard.Check("SELECT [a b] FROM t").Ok);
    }

    // ---- Final review C1 / I1: hostile text across the four dialects. Each line names the database whose tokenizer disagrees with a naive reading. ----

    public static TheoryData<string, string, string> HostileQueries => new()
    {
        // "--" not followed by a space: a comment on SQL Server / PostgreSQL / Oracle, two minus signs on MySQL. The quote then hides the rest from a MySQL-style reading.
        { "SQL Server --' then COMMIT", "SELECT 1 --'\nCOMMIT DELETE FROM t --'", "sql.unsupported_syntax" },
        { "PostgreSQL --' then a read-write transaction", "SELECT 1 --'\n; COMMIT; BEGIN READ WRITE; DELETE FROM t; COMMIT; --'", "sql.unsupported_syntax" },
        { "--- is also --x", "SELECT 1 ---x", "sql.unsupported_syntax" },
        // '#' starts a comment on MySQL only
        { "MySQL # comment hides a quote", "SELECT 1 #'\n; COMMIT; DELETE FROM t; -- '", "sql.unsupported_syntax" },
        { "MySQL # alone", "SELECT a#b FROM t", "sql.unsupported_syntax" },
        // executable comments
        { "MariaDB /*M! */", "SELECT 1 /*M! ; DELETE FROM t */", "sql.unsupported_syntax" },
        { "MariaDB /*m!", "SELECT 1 /*m!100000 , 2 */", "sql.unsupported_syntax" },
        { "MySQL /*!", "SELECT 1 /*!50000 ; DELETE FROM t */", "sql.unsupported_syntax" },
        // a lone carriage return ends a PostgreSQL "--" comment but not MySQL's
        { "lone CR inside a -- comment", "SELECT 1 -- x\r'; DELETE FROM t; -- '", "sql.unsupported_syntax" },
        { "lone CR anywhere", "SELECT 1\rFROM t", "sql.unsupported_syntax" },
        // nested block comments: PostgreSQL and SQL Server nest, MySQL and Oracle do not
        { "nested comment then a quote", "SELECT 1 /* /* */ '*/; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "/*/ opens a nested comment on PostgreSQL", "SELECT 1 /* a /*/ '; DELETE FROM t; -- '", "sql.unsupported_syntax" },
        // Unicode spaces, invisible characters and lookalikes
        { "no-break space", "SELECT\u00A01", "sql.unsupported_syntax" },
        { "zero-width space", "SELECT 1\u200B", "sql.unsupported_syntax" },
        { "line separator", "SELECT 1 -- x\u2028DELETE FROM t", "sql.unsupported_syntax" },
        { "byte order mark", "\uFEFFSELECT 1", "sql.unsupported_syntax" },
        { "modifier-letter apostrophe in a string", "SELECT 'a\u02BC; DELETE FROM t; --' FROM t", "sql.unsupported_syntax" },
        { "full-width apostrophe", "SELECT 'a\uFF07; DELETE FROM t; --' FROM t", "sql.unsupported_syntax" },
        { "typographic quote", "SELECT 'a\u2019' FROM t", "sql.unsupported_syntax" },
        { "yen sign (backslash in Shift-JIS)", "SELECT 'a\u00A5'' ; DELETE FROM t; --' FROM t", "sql.unsupported_syntax" },
        { "won sign (backslash in Korean code pages)", "SELECT 'a\u20A9' FROM t", "sql.unsupported_syntax" },
        { "full-width semicolon", "SELECT 1 \uFF1B DELETE FROM t", "sql.unsupported_syntax" },
        { "full-width letters outside quotes", "SELECT 1 \uFF24\uFF25\uFF2C\uFF25\uFF34\uFF25 FROM t", "sql.unsupported_syntax" },
        { "non-ASCII letter outside quotes", "SELECT \u0391\u03B8\u03AE\u03BD\u03B1 FROM t", "sql.unsupported_syntax" },
        { "null character", "SELECT 1\0; DELETE FROM t", "sql.unsupported_syntax" },
        { "form feed", "SELECT 1\f", "sql.unsupported_syntax" },
        { "vertical tab", "SELECT\v1", "sql.unsupported_syntax" },
        { "next line (U+0085)", "SELECT 1 -- x\u0085DELETE FROM t", "sql.unsupported_syntax" },
        // prefixed literals
        { "PostgreSQL U&'...'", "SELECT U&'d\\0061t'", "sql.unsupported_syntax" },
        { "PostgreSQL U&'...' UESCAPE", "SELECT U&'d!0061t' UESCAPE '!'", "sql.unsupported_syntax" },
        { "PostgreSQL U&\"...\"", "SELECT U&\"d!0061t\" UESCAPE '!' FROM t", "sql.unsupported_syntax" },
        { "PostgreSQL E'\\''", "SELECT E'\\''; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "Oracle q'[...]'", "SELECT q'[']' , ' ; DELETE FROM t; -- ' FROM dual", "sql.unsupported_syntax" },
        { "Oracle Q'{...}'", "SELECT Q'{'}' FROM dual", "sql.unsupported_syntax" },
        { "Oracle nq'...'", "SELECT nq'['; DELETE FROM t; --]' FROM dual", "sql.unsupported_syntax" },
        { "MySQL _utf8'...'", "SELECT _utf8'a' FROM t", "sql.unsupported_syntax" },
        // [name] and `name`: names on SQL Server / MySQL, a subscript and an operator on PostgreSQL
        { "PostgreSQL subscript holding a quote", "SELECT a[']' ; DELETE FROM t ; --']", "sql.unsupported_syntax" },
        { "SQL Server ]] escape", "SELECT [a]]; DELETE FROM t; --] FROM t", "sql.unsupported_syntax" },
        { "quote inside [name]", "SELECT [x'] , '; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "comment inside [name]", "SELECT [a--b] FROM t", "sql.unsupported_syntax" },
        { "backtick holding a quote", "SELECT `'`, '`; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "doubled backtick", "SELECT `a``b` FROM t", "sql.unsupported_syntax" },
        { "$ in [name]", "SELECT [a$$b] FROM t", "sql.unsupported_syntax" },
        // dollar quoting, also after a number (PostgreSQL before 15 reads 1$$ as 1 and a dollar quote)
        { "number then $$", "SELECT 1$$'$$; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "$$ quote", "SELECT $$'$$; DELETE FROM t; --'", "sql.unsupported_syntax" },
        { "$1 placeholder", "SELECT a FROM t WHERE b = $1", "sql.unsupported_syntax" },
        // backslashes
        { "MySQL backslash escape", "SELECT 'a\\'' ; DELETE FROM t ; --'", "sql.unsupported_syntax" },
        { "MySQL \\N outside quotes", "SELECT \\N", "sql.unsupported_syntax" },
        { "backslash in a double-quoted MySQL string", "SELECT \"a\\\"; DELETE FROM t; --\"", "sql.unsupported_syntax" },
        // a number glued to a word: SQL Server reads "1DELETE" as 1 then DELETE (a second statement)
        { "SQL Server 1DELETE", "SELECT 1DELETE FROM t", "sql.unsupported_syntax" },
        { "SQL Server 1.DELETE", "SELECT 1.DELETE FROM t", "sql.unsupported_syntax" },
        { "hex glued to a word", "SELECT 0x1DELETE FROM t", "sql.unsupported_syntax" },
        { "exponent glued to a word", "SELECT 1e5x", "sql.unsupported_syntax" },
        { "number glued to a string", "SELECT 1'a'", "sql.unsupported_syntax" },
        // @ after a name: SQL Server names may contain @, Oracle uses it for database links
        { "SQL Server a@DELETE", "SELECT a@DELETE FROM t", "sql.unsupported_syntax" },
        { "Oracle database link", "SELECT * FROM t@remote", "sql.unsupported_syntax" },
        { "@ after a quoted name", "SELECT \"t\"@remote", "sql.unsupported_syntax" },
        { "MySQL @'quoted' user variable", "SELECT @'x'", "sql.bad_parameter" },
        // Oracle / Npgsql :name binds would collide with the synthetic :p1..:pN
        { ":name bind", "SELECT a FROM t WHERE b = :p1", "sql.unsupported_syntax" },
        // ODBC escapes
        { "ODBC escape", "SELECT {fn NOW()}", "sql.unsupported_syntax" },
        // too long
        { "very long input", "SELECT 1 -- " + new string('x', SqlGuard.MaxLength), "sql.too_long" },
        // SQL Server statement verbs: T-SQL needs no ';' between statements
        { "T-SQL COMMIT", "SELECT 1 COMMIT", "sql.not_select" },
        { "T-SQL COMMIT then DELETE", "SELECT 1 COMMIT DELETE FROM t", "sql.not_select" },
        { "T-SQL ROLLBACK", "SELECT 1 ROLLBACK", "sql.not_select" },
        { "T-SQL BEGIN TRAN", "SELECT 1 BEGIN TRAN", "sql.not_select" },
        { "T-SQL SAVE TRAN", "SELECT 1 SAVE TRAN x", "sql.not_select" },
        { "T-SQL WAITFOR", "SELECT 1 WAITFOR DELAY '00:00:59'", "sql.not_select" },
        { "T-SQL KILL", "SELECT 1 KILL 53", "sql.not_select" },
        { "T-SQL SHUTDOWN", "SELECT 1 SHUTDOWN", "sql.not_select" },
        { "T-SQL DENY", "SELECT 1 DENY SELECT ON t TO public", "sql.not_select" },
        { "T-SQL BACKUP", "SELECT 1 BACKUP DATABASE x TO DISK = 'x.bak'", "sql.not_select" },
        { "T-SQL RESTORE", "SELECT 1 RESTORE DATABASE x FROM DISK = 'x.bak'", "sql.not_select" },
        { "T-SQL DBCC", "SELECT 1 DBCC FREEPROCCACHE", "sql.not_select" },
        { "T-SQL RECONFIGURE", "SELECT 1 RECONFIGURE", "sql.not_select" },
        { "T-SQL RECEIVE", "SELECT 1 RECEIVE * FROM q", "sql.not_select" },
        { "T-SQL SEND", "SELECT 1 SEND ON CONVERSATION @h (0x01)", "sql.not_select" },
        { "T-SQL END CONVERSATION", "SELECT 1 END CONVERSATION @h", "sql.not_select" },
        { "T-SQL WRITETEXT", "SELECT 1 WRITETEXT t.c @p 'x'", "sql.not_select" },
        { "T-SQL UPDATETEXT", "SELECT 1 UPDATETEXT t.c @p 0 0 'x'", "sql.not_select" },
        { "T-SQL DECLARE", "SELECT 1 DECLARE @x int", "sql.not_select" },
        { "T-SQL USE", "SELECT 1 USE master", "sql.not_select" },
        { "T-SQL SET", "SELECT 1 SET IMPLICIT_TRANSACTIONS OFF", "sql.not_select" },
        { "T-SQL SETUSER", "SELECT 1 SETUSER 'dbo'", "sql.not_select" },
        { "T-SQL REVERT", "SELECT 1 REVERT", "sql.not_select" },
        { "T-SQL CHECKPOINT", "SELECT 1 CHECKPOINT", "sql.not_select" },
        { "T-SQL DISABLE TRIGGER", "SELECT 1 DISABLE TRIGGER x ON t", "sql.not_select" },
        { "T-SQL ENABLE TRIGGER", "SELECT 1 ENABLE TRIGGER x ON t", "sql.not_select" },
        { "T-SQL OPEN SYMMETRIC KEY", "SELECT 1 OPEN SYMMETRIC KEY k DECRYPTION BY PASSWORD = 'x'", "sql.not_select" },
        { "T-SQL CLOSE MASTER KEY", "SELECT 1 CLOSE MASTER KEY", "sql.not_select" },
        { "T-SQL RAISERROR WITH LOG", "SELECT 1 RAISERROR('x', 10, 1) WITH LOG", "sql.not_select" },
        { "T-SQL WHILE", "SELECT 1 WHILE 1 = 1 SELECT 2", "sql.not_select" },
        { "T-SQL ADD SIGNATURE", "SELECT 1 ADD SIGNATURE TO p BY CERTIFICATE c", "sql.not_select" },
        { "T-SQL OPENROWSET", "SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x', 'SELECT 1')", "sql.not_select" },
        { "T-SQL OPENQUERY", "SELECT * FROM OPENQUERY(remote, 'SELECT 1')", "sql.not_select" },
        { "T-SQL OPENDATASOURCE", "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "sql.not_select" },
        { "T-SQL BULK", "SELECT 1 BULK INSERT t FROM 'f'", "sql.not_select" },
        { "T-SQL EXECUTE AS", "SELECT 1 EXECUTE AS USER = 'dbo'", "sql.not_select" },
        // the other databases' verbs (after a ';' anyway, refused as words too)
        { "MySQL LOCK IN SHARE MODE", "SELECT a FROM t LOCK IN SHARE MODE", "sql.not_select" },
        { "FOR UPDATE", "SELECT a FROM t FOR UPDATE", "sql.not_select" },
        { "PostgreSQL VACUUM", "SELECT 1 VACUUM", "sql.not_select" },
        { "PostgreSQL COPY", "WITH x AS (SELECT 1) COPY t TO STDOUT", "sql.not_select" },
        { "MySQL HANDLER", "SELECT 1 HANDLER t OPEN", "sql.not_select" },
    };

    [Theory]
    [MemberData(nameof(HostileQueries))]
    public void Refuses_hostile_text(string why, string sql, string code)
    {
        var check = SqlGuard.Check(sql);
        Assert.False(check.Ok, why);
        Assert.Equal(code, check.Code);
        Assert.Empty(check.Parameters);
    }

    public static TheoryData<string, string> SafeLookingHostileQueries => new()
    {
        // Doubling the closing quote is an escape in all four databases, so the whole text below is one quoted name / one string: no second statement can start.
        { "doubled double quote", "SELECT \"a\"\"; DELETE FROM t; --\" FROM t" },
        { "doubled single quote", "SELECT 'a''; DELETE FROM t; --' FROM t" },
        // Inside a comment or a string, a keyword is only text.
        { "keyword inside a block comment", "SELECT 1 /* ; DELETE FROM t */ FROM t" },
        { "keyword inside a line comment", "SELECT 1 -- ; DELETE FROM t\nFROM t" },
        { "CRLF ends a line comment everywhere", "SELECT 1 -- note\r\nFROM t" },
        { "tab after --", "SELECT 1 --\tnote" },
        { "-- at the very end", "SELECT 1 --" },
        { "MySQL / Oracle optimizer hint", "SELECT /*+ INDEX(t i) */ a FROM t" },
        { "Greek text in a string, a double-quoted name and a comment", "SELECT \"\u03A0\u03CC\u03BB\u03B7\" FROM t WHERE c = '\u0391\u03B8\u03AE\u03BD\u03B1' -- \u03C3\u03C7\u03CC\u03BB\u03B9\u03BF" },
    };

    [Theory]
    [MemberData(nameof(SafeLookingHostileQueries))]
    public void Accepts_text_that_every_database_reads_the_same_way(string why, string sql)
    {
        Assert.True(SqlGuard.Check(sql).Ok, why);
    }

    [Theory]
    [InlineData("SELECT TOP 10 a.id, b.[Holder Name] FROM dbo.Accounts a JOIN dbo.Holders b ON b.id = a.holder_id WHERE a.id = @id ORDER BY a.id OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT CAST(x AS decimal(10,2)) AS amount FROM t WHERE d >= '2026-01-01' AND n = N'x'")]
    [InlineData("SELECT a::text, b::int FROM app.t WHERE c = @c")]
    [InlineData("SELECT * FROM t WHERE a <> 1.5e3 AND b = -1 AND c = 0x1F AND d = .5 AND e = 1.")]
    [InlineData("SELECT id FROM app.customers START WITH id = @id CONNECT BY PRIOR id = parent_id")]
    [InlineData("SELECT `account no` FROM accounts WHERE id = @id LIMIT 1")]
    [InlineData("SELECT a FROM t WHERE b IN (SELECT c FROM u) UNION ALL SELECT a FROM v")]
    [InlineData("SELECT a FROM t FOR SHARE")]
    [InlineData("SELECT @@ROWCOUNT, a / b * c % d FROM t")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x;")]
    [InlineData("SELECT a FROM t WHERE b = X'0F' OR b = B'01' OR c = E'x'")]
    public void Accepts_ordinary_queries(string sql)
    {
        Assert.True(SqlGuard.Check(sql).Ok, SqlGuard.Check(sql).Message);
    }

    [Fact]
    public void Every_forbidden_word_is_refused_as_a_bare_word_and_accepted_when_quoted()
    {
        foreach (var word in SqlGuard.ForbiddenWords)
        {
            Assert.Equal("sql.not_select", SqlGuard.Check($"SELECT a {word.ToLowerInvariant()} FROM t").Code);
            Assert.True(SqlGuard.Check($"SELECT \"{word}\" FROM t").Ok, word);
        }
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE id = @id", "SELECT a FROM t WHERE id = :id")]
    [InlineData("SELECT '@id', \"@id\", [@id], `@id` FROM t WHERE x = @x AND y = @x", "SELECT '@id', \"@id\", [@id], `@id` FROM t WHERE x = :x AND y = :x")]
    [InlineData("SELECT 1 -- @id\nFROM t WHERE a = @a /* @b */", "SELECT 1 -- @id\nFROM t WHERE a = :a /* @b */")]
    [InlineData("SELECT @@ROWCOUNT, 'it''s @x', @y", "SELECT @@ROWCOUNT, 'it''s @x', :y")]
    [InlineData("SELECT a FROM t", "SELECT a FROM t")]
    public void RewriteParameters_changes_only_placeholders_outside_text_and_comments(string sql, string expected)
    {
        Assert.Equal(expected, SqlGuard.RewriteParameters(sql, ":"));
    }

    [Fact]
    public void RewriteParameters_with_at_prefix_is_identity()
    {
        const string sql = "SELECT a FROM t WHERE id = @id AND '@x' = b";
        Assert.Equal(sql, SqlGuard.RewriteParameters(sql, "@"));
    }

    [Fact]
    public void RewriteParametersNumbered_numbers_by_first_use_and_reuses_a_repeated_name()
    {
        var sql = SqlGuard.RewriteParametersNumbered("SELECT '@no' FROM t WHERE b = @b AND a = @a AND c = @b -- @z\n AND d = @@ROWCOUNT", ":", out var names);
        Assert.Equal("SELECT '@no' FROM t WHERE b = :p1 AND a = :p2 AND c = :p1 -- @z\n AND d = @@ROWCOUNT", sql);
        Assert.Equal(["b", "a"], names);
    }

    [Fact]
    public void RewriteParametersNumbered_treats_names_differing_in_case_as_different_and_handles_long_names()
    {
        var longName = new string('x', 64);
        var sql = SqlGuard.RewriteParametersNumbered($"SELECT 1 WHERE a = @Id AND b = @id AND c = @{longName}", ":", out var names);
        Assert.Equal("SELECT 1 WHERE a = :p1 AND b = :p2 AND c = :p3", sql);
        Assert.Equal(["Id", "id", longName], names);
    }
}
