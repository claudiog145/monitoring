using System.Text.RegularExpressions;
using FakeAppInsights.Rewrite;

namespace FakeAppInsights.Tests;

public class SchemaKqlTests
{
    [Fact]
    public void SchemaKql_DeclaresExactlyTheSixAppInsightsTables()
    {
        var commands = File.ReadAllLines(FindSchemaFile())
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var parsed = commands.Select(c => Regex.Match(c, @"^\.create-merge table (?<table>\w+) \((?<columns>[^)]*)\)$")).ToList();
        Assert.All(parsed, m => Assert.True(m.Success, "Every command must be '.create-merge table T (cols)'."));

        var tables = parsed.ToDictionary(m => m.Groups["table"].Value, m => m.Groups["columns"].Value);
        Assert.Equal(AppInsightsSchema.TableNames, tables.Keys.ToList());

        foreach (var (table, columns) in AppInsightsSchema.Tables)
        {
            var expected = string.Join(", ", columns.Select(c => $"{c.Name}:{c.Type}"));
            Assert.Equal(expected, tables[table]);
        }
    }

    [Fact]
    public void SchemaKql_RequestsCarriesTheContractColumns()
    {
        var requests = AppInsightsSchema.Tables["requests"].ToDictionary(c => c.Name, c => c.Type);

        Assert.Equal("datetime", requests["timestamp"]);
        Assert.Equal("real", requests["duration"]);
        Assert.Equal("bool", requests["success"]);
        Assert.Equal("dynamic", requests["customDimensions"]);
        Assert.Equal("int", requests["itemCount"]);
        Assert.Equal("string", requests["client_CountryOrRegion"]);
        Assert.Equal(AppInsightsSchema.Tables["requests"].Count, requests.Count);
    }

    internal static string FindSchemaFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "demo", "kusto", "schema.kql");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "Portal", "demo", "kusto", "schema.kql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Portal/demo/kusto/schema.kql not found above " + AppContext.BaseDirectory);
    }
}
