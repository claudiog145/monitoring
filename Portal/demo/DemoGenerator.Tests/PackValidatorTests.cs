using DemoGenerator.Packs;

namespace DemoGenerator.Tests;

public class PackValidatorTests
{
    [Fact]
    public void PackValidator_FixturePackIsValid()
    {
        var errors = PackValidator.Validate(Fixtures.PackDir);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void PackValidator_DetectsUncoveredStepUrl()
    {
        using var pack = new PackCopy();
        pack.Write("archetype.json", pack.Read("archetype.json").Replace("\"url\": \"/checkout/pay\"", "\"url\": \"/checkout/refund\"", StringComparison.Ordinal));

        var errors = PackValidator.Validate(pack.Dir);

        var error = Assert.Single(errors);
        Assert.StartsWith("profile.json: archetype step 'POST /checkout/pay' (url /checkout/refund, app demo-fixture-web) is not covered", error, StringComparison.Ordinal);
        Assert.Contains("path-contains \"/checkout/refund\"", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackValidator_DetectsMissingSourceForApp()
    {
        using var pack = new PackCopy();
        var sources = pack.Read("sources.json");
        var api = sources.IndexOf("{ \"alias\": \"fixture-api\"", StringComparison.Ordinal);
        pack.Write("sources.json", sources[..sources.LastIndexOf(',', api)] + "\n]\n");

        var errors = PackValidator.Validate(pack.Dir);

        Assert.Contains(errors, e => e.StartsWith("sources.json: archetype app 'demo-fixture-api' (database 'fixture-api') has no source", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("service 'inventory' lists no alias of sources.json", StringComparison.Ordinal));
    }

    [Fact]
    public void PackValidator_RejectsResourceIdThePlatformRefuses()
    {
        using var pack = new PackCopy();
        pack.Write("sources.json", pack.Read("sources.json")
            .Replace("00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo/providers/microsoft.insights/components/fixture-web",
                "00000000-0000-0000-0000-00000000demo/resourceGroups/rg-demo/providers/microsoft.insights/components/fixture-web", StringComparison.Ordinal)
            .Replace("components/fixture-api", "components/fixture-other", StringComparison.Ordinal));

        var errors = PackValidator.Validate(pack.Dir);

        Assert.Contains(errors, e => e.StartsWith("sources.json: [0].resourceId", StringComparison.Ordinal) && e.Contains("hexadecimal", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("points at component 'fixture-other' but app 'demo-fixture-api' lives in database 'fixture-api'", StringComparison.Ordinal));
    }

    [Fact]
    public void PackValidator_DetectsProfileGaps()
    {
        using var pack = new PackCopy();
        pack.Write("profile.json", pack.Read("profile.json")
            .Replace("\"key\": \"StoreId\"", "\"key\": \"ShopId\"", StringComparison.Ordinal)
            .Replace("{ \"key\": \"inventory-db\", \"displayName\": \"Inventory database\", \"matchers\": [ { \"kind\": \"dependency-type-equals\", \"value\": \"SQL\" } ] }",
                "{ \"key\": \"inventory-db\", \"displayName\": \"Inventory database\", \"matchers\": [ { \"kind\": \"dependency-type-equals\", \"value\": \"HTTP\" } ] }", StringComparison.Ordinal)
            .Replace("\"kind\": \"operation-equals\"", "\"kind\": \"operation-is\"", StringComparison.Ordinal));

        var errors = PackValidator.Validate(pack.Dir);

        Assert.Contains(errors, e => e.Contains("no custom-dimension source with key 'StoreId'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no matcher of partner 'inventory-db' matches dependency target 'sql-inv.fixtureshop.internal'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("matchers[0].kind 'operation-is' is unknown", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("archetype step 'GET /api/stock'", StringComparison.Ordinal));
    }

    [Fact]
    public void PackValidator_DetectsKpiAndCorpusProblems()
    {
        using var pack = new PackCopy();
        pack.Write("kpis.json", pack.Read("kpis.json")
            .Replace("\"bucketSize\": \"00:05:00\"", "\"bucketSize\": \"01:00:00\"", StringComparison.Ordinal)
            .Replace("name == \\\"StockChecked\\\"", "name == \\\"Other\\\"", StringComparison.Ordinal)
            .Replace("app('{{SOURCE:fixture-api}}').customEvents", "app('{{SOURCE:someone-else}}').customEvents | union database('x').customEvents", StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(Path.Combine(pack.Dir, "corpus", "Inventory"))) File.Delete(file);

        var errors = PackValidator.Validate(pack.Dir);

        Assert.Contains(errors, e => e.Contains("bucketSize must be 00:05:00", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("references alias 'someone-else'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("calls database()", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("kpis.json: needs at least 2 Business KPIs", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("corpus: 6 RCA document(s)", StringComparison.Ordinal));
    }
}
