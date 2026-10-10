using System.Text.RegularExpressions;
using WeChatAiCopilot.Api.Platform.Migrations;

namespace WeChatAiCopilot.PlatformTests;

public sealed class MigrationRunnerTests
{
    [Fact]
    public void ReadEmbeddedMigrationsReturnsGaplessNumericOrder()
    {
        var migrations = MigrationRunner.ReadEmbeddedMigrations();

        Assert.NotEmpty(migrations);
        Assert.All(migrations, migration =>
        {
            Assert.Matches(new Regex("^[0-9]{3}_.+\\.sql$", RegexOptions.CultureInvariant), migration.Version + ".sql");
            Assert.False(string.IsNullOrWhiteSpace(migration.Sql));
        });

        var prefixes = migrations
            .Select(migration => migration.Version[..3])
            .ToArray();
        Assert.Equal(prefixes.Order(StringComparer.Ordinal), prefixes);
        Assert.Equal(prefixes.Length, prefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Enumerable.Range(1, prefixes.Length).Select(version => version.ToString("D3")),
            prefixes);
    }
}
