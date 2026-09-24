using Nadlan.Persistence.MySql;

namespace Nadlan.Core.Tests;

public class MigrationScriptTests
{
    [Fact]
    public void Semicolons_inside_strings_and_comments_do_not_split()
    {
        var statements = SchemaMigrator.SplitStatements("""
            -- header; with a semicolon
            CREATE TABLE t (a INT COMMENT 'x; y', b VARCHAR(5) DEFAULT 'it''s;');
            INSERT INTO t (b) VALUES ('a\';b');
            """);

        Assert.Equal(2, statements.Count);
        Assert.Contains("'x; y'", statements[0]);
        Assert.Contains("'it''s;'", statements[0]);
        Assert.Contains(@"'a\';b'", statements[1]);
    }

    [Fact]
    public void Every_embedded_script_splits_into_complete_statements()
    {
        var scripts = SchemaMigrator.SplitAllScripts().ToList();

        Assert.NotEmpty(scripts);
        foreach (var (script, statements) in scripts)
        {
            Assert.NotEmpty(statements);
            foreach (var statement in statements)
            {
                Assert.True(
                    statement.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
                    statement.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase) ||
                    statement.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase),
                    $"{script}: unexpected statement start: {statement[..Math.Min(80, statement.Length)]}");
                Assert.Equal(statement.Count(c => c == '('), statement.Count(c => c == ')'));
            }
        }
    }
}
