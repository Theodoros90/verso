using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class SqlGuardWriteTests
{
    [Theory]
    [InlineData("INSERT INTO audit (a, b) VALUES (@a, @b)", "INSERT", "a,b")]
    [InlineData("UPDATE t SET a = @a WHERE id = @id", "UPDATE", "a,id")]
    [InlineData("DELETE FROM t WHERE id = @id", "DELETE", "id")]
    [InlineData("insert into t (a) values (@a);", "INSERT", "a")]
    [InlineData("update dbo.t set a = @a, b = 'x' where id = @id -- note", "UPDATE", "a,id")]
    [InlineData("/* lead */ delete t where id = @id", "DELETE", "id")]
    [InlineData("INSERT t (a) VALUES (1)", "INSERT", "")]
    public void Accepts_insert_update_delete(string sql, string verb, string parameters)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.True(r.Ok, r.Message);
        Assert.Null(r.Code);
        Assert.Equal(verb, r.Verb);
        Assert.Equal(parameters, string.Join(",", r.Parameters));
    }

    [Fact]
    public void Accepts_insert_select()
    {
        var r = SqlGuard.CheckWrite("INSERT INTO t (a) SELECT a FROM u WHERE x = @x");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("INSERT", r.Verb);
        Assert.Equal("x", Assert.Single(r.Parameters));
    }

    [Fact]
    public void Accepts_subselect_in_where()
    {
        var r = SqlGuard.CheckWrite("UPDATE t SET a = 1 WHERE id IN (SELECT id FROM u WHERE x = @x)");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("UPDATE", r.Verb);
        Assert.Equal("x", Assert.Single(r.Parameters));
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN u ON u.id = t.id WHERE u.x = @x")]
    [InlineData("UPDATE t1 JOIN (SELECT id FROM u WHERE x = 1) s ON s.id = t1.id SET t1.a = 1 WHERE t1.b = 2")]
    [InlineData("DELETE t FROM t JOIN u ON u.id = t.id WHERE u.x = @x")]
    [InlineData("UPDATE t SET \"where\" = 1, [set] = 2 WHERE id = 1")]
    [InlineData("INSERT INTO t (a) VALUES ((SELECT MAX(a) FROM u WHERE b = 1))")]
    [InlineData("UPDATE t SET a = 1 WHERE CURRENT_TIMESTAMP > d")]
    public void Accepts_ordinary_writes(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.True(r.Ok, r.Message);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t WHERE 1 = 1")]
    [InlineData("MERGE t USING s ON 1=1 WHEN MATCHED THEN DELETE")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("DROP TABLE t")]
    [InlineData("EXEC p")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("CALL p()")]
    [InlineData("(INSERT INTO t VALUES (1))")]
    [InlineData("'x' INSERT INTO t VALUES (1)")]
    [InlineData("REPLACE INTO t VALUES (1)")]
    [InlineData("UPSERT INTO t VALUES (1)")]
    public void Rejects_read_and_other_verbs(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
        Assert.Equal("Only an INSERT, UPDATE or DELETE statement is allowed.", r.Message);
        Assert.Empty(r.Parameters);
        Assert.Null(r.Verb);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 DELETE FROM u WHERE id = 2")]
    [InlineData("INSERT INTO t VALUES (1) INSERT INTO t VALUES (2)")]
    [InlineData("DELETE FROM t WHERE id = 1 UPDATE u SET a = 1 WHERE id = 2")]
    [InlineData("INSERT INTO t VALUES (1) DELETE FROM t WHERE 1 = 1")]
    [InlineData("INSERT INTO t SELECT a FROM u WHERE b IN (DELETE FROM v WHERE 1 = 1)")]
    [InlineData("INSERT INTO t EXEC p")]
    [InlineData("INSERT INTO t EXECUTE p")]
    [InlineData("INSERT INTO t VALUES (1) MERGE u USING s ON 1 = 1")]
    [InlineData("INSERT INTO t VALUES (1) TRUNCATE TABLE u")]
    [InlineData("INSERT INTO t VALUES (1) DROP TABLE u")]
    public void Rejects_second_dml_without_semicolon(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1) COMMIT")]
    [InlineData("INSERT INTO t VALUES (1) COMMIT DELETE FROM u WHERE 1 = 1")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 ROLLBACK")]
    [InlineData("DELETE FROM t WHERE id = 1 BEGIN TRAN")]
    [InlineData("DELETE FROM t WHERE id = 1 SAVE TRAN x")]
    [InlineData("INSERT INTO t VALUES (1) SAVEPOINT s")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 SET IMPLICIT_TRANSACTIONS OFF")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 WAITFOR DELAY '00:00:59'")]
    public void Rejects_transaction_verbs_after_write(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("delete t;")]
    [InlineData("UPDATE t SET a = (SELECT 1 WHERE 1=1)")]
    [InlineData("DELETE FROM t WHERE_x = 1")]
    [InlineData("DELETE FROM t AS where_clause")]
    [InlineData("UPDATE t SET a = 'WHERE x = 1'")]
    [InlineData("DELETE FROM t -- WHERE id = 1")]
    [InlineData("DELETE FROM t /* WHERE id = 1 */")]
    [InlineData("UPDATE t SET \"WHERE\" = 1")]
    [InlineData("UPDATE t SET [WHERE] = 1")]
    [InlineData("UPDATE t SET `WHERE` = 1")]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN (SELECT id FROM u WHERE x = 1) s ON s.id = t.id")]
    public void Rejects_update_and_delete_without_where(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.no_where", r.Code);
        Assert.Equal("UPDATE and DELETE need a WHERE condition, so a statement can never change a whole table by accident.", r.Message);
    }

    [Fact]
    public void Insert_needs_no_where()
    {
        Assert.True(SqlGuard.CheckWrite("INSERT INTO t VALUES (1)").Ok);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1 INTO x WHERE 1=1")]
    [InlineData("DELETE FROM t OUTPUT deleted.* INTO x WHERE id = 1")]
    [InlineData("INSERT INTO t SELECT a INTO x FROM u")]
    [InlineData("INSERT INTO INTO t VALUES (1)")]
    [InlineData("INSERT t SELECT a INTO u FROM v")]
    [InlineData("INSERT /* hint */ t INTO u VALUES (1)")]
    [InlineData("INSERT INTO t OUTPUT inserted.id INTO u VALUES (1)")]
    public void Rejects_into_outside_insert(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.select_into", r.Code);
    }

    [Fact]
    public void Into_right_after_insert_is_allowed_even_after_a_comment()
    {
        Assert.True(SqlGuard.CheckWrite("INSERT /*+ APPEND */ INTO t VALUES (1)").Ok);
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1) ON DUPLICATE KEY UPDATE a = 2")]
    [InlineData("INSERT INTO t (a) VALUES (1) ON CONFLICT (a) DO UPDATE SET a = 2")]
    [InlineData("INSERT INTO t (a) VALUES (1) ON CONFLICT DO NOTHING")]
    public void Rejects_on_duplicate_key_update(string sql)
    {
        // Conservative: an upsert is refused because UPDATE (or DO) is a second statement word.
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
    }

    [Theory]
    [InlineData("INSERT INTO t SET a = 1")]
    [InlineData("DELETE FROM t WHERE id = 1 SET a = 1")]
    [InlineData("UPDATE t SET a = 1 SET b = 2 WHERE id = 1")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 SET NOCOUNT ON")]
    [InlineData("UPDATE t SET a = (SELECT 1 SET) WHERE id = 1")]
    public void Set_is_allowed_once_and_only_as_the_set_clause_of_an_update(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
    }

    [Theory]
    // T-SQL UPDATE STATISTICS is maintenance, not an UPDATE; without ';' it chains: UPDATE STATISTICS t / SET NOCOUNT ON / SELECT 1 WHERE 1=1
    [InlineData("UPDATE STATISTICS t SET NOCOUNT ON SELECT 1 FROM u WHERE 1 = 1")]
    [InlineData("UPDATE STATISTICS t SELECT 1 FROM u WHERE 1 = 1")]
    [InlineData("update /* x */ statistics t WITH FULLSCAN SELECT 1 FROM u WHERE 1 = 1")]
    // every UPDATE in the four databases has a SET clause
    [InlineData("UPDATE t WHERE id = 1")]
    [InlineData("UPDATE t SELECT 1 FROM u WHERE 1 = 1")]
    public void Rejects_update_statistics_and_update_without_set(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok);
        Assert.Equal("sql.not_write", r.Code);
    }

    // ---- Review fix round 1: SQL Server runs a second statement after a DML statement without ';'. A trailing SELECT's WHERE must not satisfy the WHERE rule,
    // and a trailing read must not run either. ----

    [Theory]
    // critical: the WHERE belongs to a second statement, the write changes the whole table
    [InlineData("DELETE FROM t SELECT 1 WHERE 1 = 1")]
    [InlineData("UPDATE t SET a = 1 SELECT 1 WHERE 1 = 1")]
    [InlineData("DELETE FROM t SELECT 1 FROM u WHERE 1 = 1")]
    [InlineData("UPDATE t SET a = 1 IF 1 = 1 SELECT 1 WHERE 1 = 1")]
    [InlineData("DELETE t RETURN SELECT 1 WHERE 1 = 1")]
    [InlineData("update t set a = 1 /* x */ select 1 where 1 = 1")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 SELECT 1")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u) UNION SELECT 1")]
    // important: a second, reading statement after an INSERT
    [InlineData("INSERT INTO t VALUES (1) SELECT name FROM sys.sql_logins")]
    [InlineData("INSERT INTO t VALUES (1) SELECT * FROM u WITH (TABLOCKX, HOLDLOCK)")]
    [InlineData("INSERT INTO t VALUES (1) SELECT NEXT VALUE FOR s")]
    [InlineData("INSERT INTO t VALUE (1) SELECT 1")]
    [InlineData("INSERT INTO t DEFAULT VALUES SELECT 1")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u SELECT name FROM sys.sql_logins")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u WHERE 1 = 1 SELECT 1")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION ALL SELECT b FROM v SELECT 1")]
    // read-only T-SQL statement verbs that start a statement without ';'
    [InlineData("INSERT INTO t VALUES (1) IF 1 = 1 PRINT 'x'")]
    [InlineData("INSERT INTO t VALUES (1) PRINT 'x'")]
    [InlineData("INSERT INTO t VALUES (1) THROW 50000, 'x', 1")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURN")]
    [InlineData("DELETE FROM t WHERE id = 1 GOTO done")]
    [InlineData("DELETE FROM t WHERE id = 1 READTEXT t.c @p 0 1")]
    [InlineData("DELETE FROM t WHERE id = 1 FETCH NEXT FROM c")]
    [InlineData("DELETE FROM t WHERE id = 1 BREAK")]
    [InlineData("DELETE FROM t WHERE id = 1 CONTINUE")]
    [InlineData("DELETE FROM t WHERE id = 1 GET CONVERSATION GROUP @g FROM q")]
    [InlineData("DELETE FROM t WHERE id = 1 MOVE CONVERSATION @h TO @g")]
    // a WITH that is not a table hint starts a statement with a CTE
    [InlineData("DELETE FROM t WHERE id = 1 WITH x AS (SELECT 1 a) SELECT a FROM x")]
    [InlineData("INSERT INTO t VALUES (1) WITH x AS (SELECT 1 a) SELECT a FROM x")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 WITH /* c */ x AS (SELECT 1) SELECT 1")]
    // a query in parentheses right after a value, a ')' or a label is a second statement (checked on LocalDB: the DELETE ran, then the SELECT)
    [InlineData("DELETE FROM t WHERE id = 1 (SELECT name FROM sys.sql_logins)")]
    [InlineData("DELETE FROM t WHERE id = 1 ((SELECT 42))")]
    [InlineData("DELETE FROM t WHERE id = 1 ( /* c */ ( SELECT 42))")]
    [InlineData("DELETE FROM t WHERE id = 1 (SELECT 42) UNION (SELECT 43)")]
    [InlineData("DELETE FROM t WHERE id = @@SPID (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = @id (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = 'x' (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = [x] (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT 1) (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = 1 lbl: (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = 1 lbl: WITH x AS (SELECT 1 a) SELECT a FROM x")]
    [InlineData("INSERT INTO t VALUES (9, 9) (SELECT * FROM u WITH (TABLOCKX, HOLDLOCK))")]
    [InlineData("INSERT INTO t SELECT 9, 9 (SELECT 42)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 (WITH x AS (SELECT 1) SELECT 1)")]
    // ... and right after a word that ends an expression (checked on LocalDB: each ran as a second statement)
    [InlineData("DELETE FROM t WHERE id = 1 OR a IS NULL (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = 1 OR a IS NOT NULL ((SELECT 42))")]
    [InlineData("DELETE FROM t WHERE id = 1 OR a = CURRENT_TIMESTAMP (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = 1 OR a = USER (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE c = '1' COLLATE Latin1_General_CI_AS (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE c = '1' COLLATE Latin1_General_CI_AS(SELECT 42)")]
    [InlineData("UPDATE t SET a = CASE WHEN b = 1 THEN 1 ELSE 2 END WHERE id = 1 OR b = CASE WHEN c = 1 THEN 1 END (SELECT 42)")]
    [InlineData("DELETE FROM t WHERE id = t.id (SELECT 42)")] // a syntax error on SQL Server, refused all the same: only known names count as calls
    // a ')' without its '(' (a syntax error on SQL Server, refused so that the parenthesis depth stays meaningful)
    [InlineData("DELETE FROM t WHERE id = 1) SELECT 1")]
    public void Rejects_a_second_statement_after_the_write(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.False(r.Ok, sql);
        Assert.Equal("sql.not_write", r.Code);
        Assert.Null(r.Verb);
    }

    [Theory]
    [InlineData("UPDATE t SET a = u.b FROM t JOIN u ON u.id = t.id WHERE u.x = @x")]
    [InlineData("DELETE t FROM t JOIN u ON u.id = t.id WHERE u.x = @x")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION ALL SELECT b FROM v")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION SELECT b FROM v INTERSECT SELECT c FROM w EXCEPT SELECT d FROM x")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u MINUS SELECT b FROM v")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION /* all of them */ ALL SELECT b FROM v")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION DISTINCT SELECT b FROM v")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u UNION (SELECT b FROM v)")]
    [InlineData("UPDATE t SET a = COALESCE((SELECT MAX(b) FROM u), 0) WHERE id = 1")]
    [InlineData("UPDATE t SET a = COALESCE ( (SELECT MAX(b) FROM u), 0) WHERE id = 1")]
    [InlineData("DELETE FROM t WHERE id IN ((SELECT id FROM u)) AND b = ANY (SELECT b FROM v)")]
    [InlineData("INSERT INTO t (a) VALUES ((SELECT 1)), (2)")]
    [InlineData("UPDATE t SET a = ISNULL((SELECT MAX(b) FROM u), 0) + -(SELECT 1) * (SELECT 2) WHERE (SELECT COUNT(*) FROM v) > 0 AND NOT EXISTS(SELECT 1 FROM w)")]
    [InlineData("UPDATE t SET a = CASE (SELECT b FROM u) WHEN 1 THEN (SELECT 2) ELSE (SELECT 3) END WHERE id = 1")]
    [InlineData("INSERT INTO t (a, b) SELECT (SELECT 1), CAST((SELECT 2) AS int) FROM u JOIN (SELECT 1 x) v ON (SELECT 1) = 1")]
    [InlineData("DELETE FROM t WHERE a BETWEEN (SELECT 1) AND (SELECT 2) OR b > ALL (SELECT c FROM u) OR d = SOME (SELECT 1)")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u WHERE x = @x")]
    [InlineData("INSERT INTO t (a) OUTPUT inserted.id VALUES (@a)")]
    [InlineData("INSERT INTO t (a) VALUES (DEFAULT), (1)")]
    [InlineData("UPDATE TOP (1) t SET a = 1 WHERE b = 2")]
    [InlineData("UPDATE t SET a = CASE WHEN b = 1 THEN (SELECT MAX(c) FROM u WHERE u.id = t.id) ELSE 0 END, d = (SELECT 1) WHERE id = @id")]
    [InlineData("UPDATE t, u SET t.a = u.b WHERE t.id = u.id")]
    [InlineData("UPDATE t WITH (ROWLOCK) SET a = 1 WHERE id = 1")]
    [InlineData("DELETE FROM t WITH (ROWLOCK) WHERE id = 1")]
    [InlineData("INSERT INTO t WITH (TABLOCK) (a) SELECT a FROM u WITH (NOLOCK)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 OPTION (RECOMPILE)")]
    [InlineData("DELETE FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)")]
    [InlineData("UPDATE t SET a = 1 WHERE id IN (SELECT id FROM u UNION SELECT id FROM v)")]
    [InlineData("INSERT INTO t (a) SELECT a FROM (SELECT a FROM u ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY) s")]
    [InlineData("INSERT INTO t (a) SELECT IIF(x IS NULL, 1, 2) FROM u")]
    [InlineData("UPDATE t SET a = (SELECT IF(1, 2, 3)) WHERE id = 1")]
    public void Accepts_legitimate_write_shapes(string sql)
    {
        var r = SqlGuard.CheckWrite(sql);
        Assert.True(r.Ok, $"{sql}: {r.Message}");
    }

    [Theory]
    // Conservative and documented: at the top level these words always count as a statement verb, so MySQL's IF() function and OFFSET ... FETCH must sit
    // in parentheses (or use CASE), and a column named like one must be quoted.
    [InlineData("UPDATE t SET a = IF(b, 1, 2) WHERE id = 1")]
    [InlineData("INSERT INTO t (a) SELECT a FROM u ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData("UPDATE t SET move = 1 WHERE id = 1")]
    [InlineData("INSERT INTO t WITH x AS (SELECT 1 a) SELECT a FROM x")]
    [InlineData("INSERT INTO t (id, a) (SELECT 9, 9)")]
    public void Refuses_top_level_statement_words_even_where_a_database_would_accept_them(string sql)
    {
        Assert.Equal("sql.not_write", SqlGuard.CheckWrite(sql).Code);
    }

    [Fact]
    public void A_table_called_statistics_must_be_quoted()
    {
        Assert.True(SqlGuard.CheckWrite("UPDATE \"statistics\" SET a = 1 WHERE id = 1").Ok);
        Assert.True(SqlGuard.CheckWrite("UPDATE app.statistics SET a = 1 WHERE id = 1").Ok);
    }

    [Fact]
    public void Rejects_empty_with_a_write_message_and_too_long()
    {
        foreach (var sql in new[] { null, "", "   ", "-- only a comment", "/* x */" })
        {
            var r = SqlGuard.CheckWrite(sql);
            Assert.Equal("sql.empty", r.Code);
            Assert.Equal("Type an INSERT, UPDATE or DELETE statement.", r.Message);
        }
        Assert.Equal("sql.too_long", SqlGuard.CheckWrite("INSERT INTO t VALUES ('" + new string('a', SqlGuard.MaxLength) + "')").Code);
    }

    [Fact]
    public void Rejects_second_statement_after_semicolon()
    {
        Assert.Equal("sql.multiple_statements", SqlGuard.CheckWrite("DELETE FROM t WHERE id = 1; SELECT 1").Code);
        Assert.True(SqlGuard.CheckWrite("DELETE FROM t WHERE id = 1; -- done").Ok);
    }

    [Fact]
    public void Every_other_forbidden_word_is_refused_in_write_mode_and_accepted_when_quoted()
    {
        foreach (var word in SqlGuard.ForbiddenWords)
        {
            Assert.Equal("sql.not_write", SqlGuard.CheckWrite($"UPDATE t SET a = 1 WHERE id = 1 {word.ToLowerInvariant()}").Code);
            Assert.Equal("sql.not_write", SqlGuard.CheckWrite($"INSERT INTO t VALUES (1) {word}").Code);
            Assert.True(SqlGuard.CheckWrite($"INSERT INTO t (\"{word}\") VALUES (1)").Ok, word);
        }
    }

    // ---- Every Phase 1 dialect attack (SqlGuardTests.HostileQueries) is refused in write mode too, in every position a write statement offers. ----

    public static TheoryData<string, string, string, string?> HostileWrites
    {
        get
        {
            // {0} = the hostile text as it is (a SELECT, legal as the source of an INSERT or as a subquery), {1} = the same text without its leading "SELECT "
            // (so it continues an expression of the write). In these positions the refusal comes from the hostile part itself: same code as in read mode.
            string[] exact =
            [
                "INSERT INTO t {0}",
                "INSERT INTO t SELECT 1 UNION ALL {0}",
                "UPDATE t SET a = 1 WHERE id IN ({0})",
                "DELETE FROM t WHERE id IN ({0})",
                "INSERT INTO t VALUES ({1})",
                "UPDATE t SET a = {1} WHERE id = 1",
                "UPDATE t SET a = 1 WHERE id = {1}",
                "DELETE FROM t WHERE id = {1}",
            ];
            // After a complete write a top-level SELECT is itself refused (a second statement on SQL Server), possibly before the hostile part is reached: any refusal.
            string[] trailing =
            [
                "INSERT INTO t VALUES (1) {0}",
                "UPDATE t SET a = 1 WHERE id = 1 {0}",
                "DELETE FROM t WHERE id = 1 {0}",
            ];
            var data = new TheoryData<string, string, string, string?>();
            foreach (var row in SqlGuardTests.HostileQueries)
            {
                var why = (string)row[0];
                var sql = (string)row[1];
                var code = (string)row[2];
                var rest = sql.StartsWith("SELECT ", StringComparison.Ordinal) ? sql["SELECT ".Length..] : sql;
                // In read mode a forbidden word gives sql.not_select; in write mode the same word gives sql.not_write. Every other code is identical.
                var writeCode = code == "sql.not_select" ? "sql.not_write" : code;
                foreach (var wrapper in exact)
                    data.Add(why, wrapper, string.Format(System.Globalization.CultureInfo.InvariantCulture, wrapper, sql, rest), writeCode);
                foreach (var wrapper in trailing)
                    data.Add(why, wrapper, string.Format(System.Globalization.CultureInfo.InvariantCulture, wrapper, sql), null);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HostileWrites))]
    public void Refuses_every_phase1_dialect_attack_in_write_mode(string why, string wrapper, string sql, string? code)
    {
        var check = SqlGuard.CheckWrite(sql);
        Assert.False(check.Ok, $"{why} in {wrapper}");
        if (code is not null) Assert.Equal(code, check.Code);
        Assert.Empty(check.Parameters);
        Assert.Null(check.Verb);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1) --'\nCOMMIT DELETE FROM t --'")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 #'\n; COMMIT; DELETE FROM t; -- '")]
    [InlineData("DELETE FROM t WHERE id = 1 /*M! ; DELETE FROM u */")]
    [InlineData("DELETE FROM t WHERE id = 1 -- x\r'; DELETE FROM u; -- '")]
    [InlineData("UPDATE t SET a = 'x\\'' WHERE id = 1 ; DELETE FROM u ; --'")]
    [InlineData("UPDATE t SET a = $$'$$ WHERE id = 1; DELETE FROM u; --'")]
    [InlineData("DELETE FROM t WHERE id = 1 /* /* */ '*/; DELETE FROM u; --'")]
    [InlineData("UPDATE t SET a = q'[']' WHERE id = 1 , ' ; DELETE FROM u; -- '")]
    [InlineData("UPDATE t--'\nSET a = 1 WHERE id = 1")]
    [InlineData("DELETE FROM t WHERE id = 1DELETE FROM u WHERE 1 = 1")]
    [InlineData("DELETE FROM t\u00A0WHERE id = 1")] // U+00A0 NO-BREAK SPACE between t and WHERE
    public void Refuses_dialect_attacks_inside_the_write_itself(string sql)
    {
        var check = SqlGuard.CheckWrite(sql);
        Assert.False(check.Ok, sql);
        Assert.Equal("sql.unsupported_syntax", check.Code);
    }

    [Fact]
    public void Read_mode_unchanged()
    {
        Assert.Equal("sql.not_select", SqlGuard.Check("INSERT INTO t VALUES (1)").Code);
        Assert.Equal("sql.not_select", SqlGuard.Check("UPDATE t SET a = 1 WHERE id = 1").Code);
        Assert.Equal("sql.not_select", SqlGuard.Check("DELETE FROM t WHERE id = 1").Code);
        Assert.Equal("sql.select_into", SqlGuard.Check("SELECT a INTO t2 FROM t").Code);
        Assert.Equal("sql.not_select", SqlGuard.Check("SELECT a FROM t SET x = 1").Code);
        Assert.Equal("Type a SELECT query.", SqlGuard.Check("  ").Message);
        Assert.Equal("The query must start with SELECT or WITH.", SqlGuard.Check("(SELECT 1)").Message);
        var ok = SqlGuard.Check("SELECT a FROM t WHERE b = @b AND c = @c");
        Assert.True(ok.Ok);
        Assert.Equal(["b", "c"], ok.Parameters);
        Assert.Equal("sql.unsupported_syntax", SqlGuard.Check("SELECT 1 --'\nCOMMIT").Code);
    }
}
