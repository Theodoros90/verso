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

    public static TheoryData<string, string, string, string> HostileWrites
    {
        get
        {
            string[] wrappers =
            [
                "INSERT INTO t VALUES (1) {0}",
                "UPDATE t SET a = 1 WHERE id = 1 {0}",
                "DELETE FROM t WHERE id = 1 {0}",
                "INSERT INTO t {0}",
            ];
            var data = new TheoryData<string, string, string, string>();
            foreach (var row in SqlGuardTests.HostileQueries)
            {
                var why = (string)row[0];
                var sql = (string)row[1];
                var code = (string)row[2];
                // In read mode a forbidden word gives sql.not_select; in write mode the same word gives sql.not_write. Every other code is identical.
                var writeCode = code == "sql.not_select" ? "sql.not_write" : code;
                foreach (var wrapper in wrappers)
                    data.Add(why, wrapper, string.Format(System.Globalization.CultureInfo.InvariantCulture, wrapper, sql), writeCode);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HostileWrites))]
    public void Refuses_every_phase1_dialect_attack_in_write_mode(string why, string wrapper, string sql, string code)
    {
        var check = SqlGuard.CheckWrite(sql);
        Assert.False(check.Ok, $"{why} in {wrapper}");
        Assert.Equal(code, check.Code);
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
    [InlineData("DELETE FROM t WHERE id = 1")]
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
