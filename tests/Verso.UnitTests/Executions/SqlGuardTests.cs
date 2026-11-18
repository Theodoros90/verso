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
    public void Allows_dollar_in_identifier()
    {
        Assert.True(SqlGuard.Check("SELECT a$b FROM t WHERE x = $1").Ok);
    }

    [Fact]
    public void Rejects_double_dash_without_space_hiding_statement()
    {
        Assert.False(SqlGuard.Check("SELECT 5--1; DELETE FROM t").Ok);
        Assert.Equal("sql.multiple_statements", SqlGuard.Check("SELECT 5--1; SELECT 2").Code);
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
        Assert.True(SqlGuard.Check("SELECT [a\\b] FROM t").Ok);
    }
}
