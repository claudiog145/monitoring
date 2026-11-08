using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A4;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>Contract v4 §5 (AG3): the KQL catalog accepts custom <c>kpi:</c> queries for <c>kql:{queryKey}</c> KPIs.</summary>
public sealed class KqlQueryAdminCustomKeyTests
{
    private const string Key = "kpi:hotels.purchases-v1";

    private const string KpiQuery = """
        let fromTs = {{from}};
        let toTs = {{to}};
        app('{{SOURCE:cruise-core}}').requests
        | where timestamp >= fromTs and timestamp < toTs
        | summarize TotalCount = sum(itemCount) by BucketStart = bin(timestamp, {{bucket}})
        | project ['kind'] = "bucket", BucketStart, TotalCount, Value = todouble(TotalCount)
        """;

    private readonly InMemoryKqlQueryStore _store = new();

    private KqlQueryAdminService Build() => new(_store, new KqlQueryTextProvider(), new InMemoryTelemetrySourceStore());

    [Fact]
    public async Task KqlQueryAdmin_AcceptsKpiPrefixedCustomKey()
    {
        var service = Build();

        var (status, _) = await service.SaveAsync(Key, KpiQuery, "Hotels purchases", null, true, "ana@example.com");

        Assert.Equal(200, status);
        var stored = await _store.GetAsync(Key);
        Assert.NotNull(stored);
        Assert.False(stored.IsBuiltIn);
        Assert.Equal(KpiQuery, stored.KqlText);

        // Readable and listed like any other query, with no built-in text behind it.
        var detail = await service.GetAsync(Key);
        Assert.NotNull(detail);
        Assert.Equal(KpiQuery, detail.KqlText);
        Assert.Equal("", detail.BuiltInText);
        Assert.True(detail.HasOverride);
        Assert.Empty(detail.RequiredPlaceholders);
        Assert.Equal(["cruise-core"], detail.SourceAliases);
        Assert.Contains("kql:" + Key, detail.UsedBy);
        var listed = Assert.Single(await service.ListAsync(), q => q.QueryKey == Key);
        Assert.Equal(1, listed.Version);

        // Valid shapes of the key: lowercase, [a-z0-9:._-], at most 128 characters.
        Assert.True(KqlQueryKeys.IsCustomKpi("kpi:a"));
        Assert.True(KqlQueryKeys.IsCustomKpi("kpi:" + new string('a', 124)));
        Assert.False(KqlQueryKeys.IsCustomKpi("kpi:" + new string('a', 125)));
        foreach (var bad in new[] { "kpi:", "KPI:x", "kpi:Hotels", "kpi:a b", "kpi:a/b", "kpi:a'b", " kpi:a" })
        {
            Assert.False(KqlQueryKeys.IsCustomKpi(bad), bad);
            Assert.StartsWith("Unknown query key", await service.ValidateAsync(bad, KpiQuery));
        }

        // A kpi: query may only use the KPI tokens; the shared safety rules still apply.
        Assert.Contains("{{TIME_START}}", await service.ValidateAsync(Key, KpiQuery + " | where timestamp > {{TIME_START}}"));
        Assert.Contains("{{SOURCE:alias}}", await service.ValidateAsync(Key, "app('SomeOtherResource').requests | take 1"));
        Assert.Contains("nope", await service.ValidateAsync(Key, "app('{{SOURCE:nope}}').requests | take 1"));
        Assert.Null(await service.GetAsync("kpi:not-saved"));
    }

    [Fact]
    public async Task KqlQueryAdmin_RejectsUnprefixedUnknownKey()
    {
        var service = Build();

        var (status, payload) = await service.SaveAsync("hotels-purchases", KpiQuery, null, null, true, "ana@example.com");

        Assert.Equal(400, status);
        var error = (string)payload.GetType().GetProperty("error")!.GetValue(payload)!;
        Assert.StartsWith("Unknown query key \"hotels-purchases\".", error);
        Assert.Contains("\"kpi:\"", error);
        Assert.Empty(await _store.GetAllAsync());
        Assert.Null(await service.GetAsync("hotels-purchases"));
        Assert.Equal(404, (await service.RevertAsync("hotels-purchases", "ana@example.com")).StatusCode);

        // Built-in validation is unchanged: a built-in key still needs its placeholders, and is not
        // subject to the KPI token allow-list.
        var builtIn = await service.ValidateAsync(KqlQueryKeys.ScoutDrillDown, "requests | take 1");
        Assert.Contains("{{OPERATION_IDS}}", builtIn);
        Assert.Null(await service.ValidateAsync(KqlQueryKeys.ScoutDrillDown,
            "requests | where operation_Id in ({{OPERATION_IDS}}) | where timestamp >= {{TIME_START}} and timestamp < {{TIME_END}} | take {{MAX_ROWS}}"));
    }

    [Theory]
    [InlineData("app(@'8644ddf0-0000-0000-0000-000000000000').requests | take 1")]
    [InlineData("workspace('00000000-0000-0000-0000-000000000000').AppRequests | take 1")]
    [InlineData("app ('x').requests | take 1")]
    [InlineData("app(h'x').requests | take 1")]
    [InlineData("resource('/subscriptions/0000/resourceGroups/rg/providers/microsoft.insights/components/c').requests | take 1")]
    public async Task KqlQueryAdmin_CustomKeyRejectsCrossResourceCalls(string text)
    {
        var (status, payload) = await Build().SaveAsync(Key, text, null, null, true, "ana@example.com");

        Assert.Equal(400, status);
        Assert.Contains("A KPI query may not call", (string)payload.GetType().GetProperty("error")!.GetValue(payload)!);
        Assert.Null(await _store.GetAsync(Key));

        // A legit query that only mentions "application" in a string or a column name is accepted.
        var legit = KpiQuery + "\n| extend application = \"application('x')\", application_Version = 1";
        Assert.Null(await Build().ValidateAsync(Key, legit));
    }

    [Fact]
    public async Task KqlQueryAdmin_CustomKeyInUse_RevertConflictsAndEditIsDryRun()
    {
        var definitions = new A4FakeKpiDefinitionStore();
        var dryRuns = new List<(int Id, string Text)>();
        string? dryRunResult = null;
        var usage = new KqlQueryUsage(definitions, (d, text, _) =>
        {
            dryRuns.Add((d.KpiDefinitionId, text));
            return Task.FromResult(dryRunResult);
        });
        var service = new KqlQueryAdminService(_store, new KqlQueryTextProvider(), new InMemoryTelemetrySourceStore(), usage);

        // Not used yet: saved without a dry run.
        Assert.Equal(200, (await service.SaveAsync(Key, KpiQuery, null, null, true, "ana@example.com")).StatusCode);
        Assert.Empty(dryRuns);

        definitions.Definitions.Add(A4Http.Definition(7, KpiPillar.Business, enabled: false) with { SourceKey = "kql:" + Key });
        definitions.Definitions.Add(A4Http.Definition(9, KpiPillar.Business, enabled: true) with { SourceKey = "kql:" + Key });
        definitions.Definitions.Add(A4Http.Definition(11, KpiPillar.Business, enabled: true) with { SourceKey = "kql:kpi:other" });

        // An edit is dry-run for an enabled KPI that uses the query, with the new text.
        var edited = KpiQuery + "\n| where TotalCount > 0";
        Assert.Equal(200, (await service.SaveAsync(Key, edited, null, null, true, "ana@example.com")).StatusCode);
        Assert.Equal((9, edited), Assert.Single(dryRuns));

        // A failing dry run keeps the stored text.
        dryRunResult = "the query output does not match the KPI output contract";
        var (status, payload) = await service.SaveAsync(Key, KpiQuery + "\n| project Broken = 1", null, null, true, "bob@example.com");
        Assert.Equal(400, status);
        Assert.StartsWith("Query is used by KPI 9; the dry run of the new text failed:", (string)payload.GetType().GetProperty("error")!.GetValue(payload)!);
        Assert.Equal(edited, (await _store.GetAsync(Key))!.KqlText);

        // Deleting it while an enabled KPI uses it is a conflict; disabled users do not block.
        var revert = await service.RevertAsync(Key, "carl@example.com");
        Assert.Equal(409, revert.StatusCode);
        Assert.Equal("Query is used by KPI(s) 9; disable them first.", (string)revert.Payload.GetType().GetProperty("error")!.GetValue(revert.Payload)!);
        Assert.NotNull(await _store.GetAsync(Key));

        definitions.Definitions.RemoveAll(d => d.KpiDefinitionId == 9);
        Assert.Equal(200, (await service.RevertAsync(Key, "carl@example.com")).StatusCode);
        Assert.Null(await _store.GetAsync(Key));
    }

    [Fact]
    public async Task KqlQueryAdmin_CustomKeyAudited()
    {
        var service = Build();

        await service.SaveAsync(Key, KpiQuery, "v1", null, true, "ana@example.com");
        await service.SaveAsync(Key, KpiQuery + "\n| where TotalCount > 0", "v2", null, true, "bob@example.com");
        var (revertStatus, revertPayload) = await service.RevertAsync(Key, "carl@example.com");

        Assert.Equal(200, revertStatus);
        Assert.Equal(true, revertPayload.GetType().GetProperty("deleted")!.GetValue(revertPayload));
        Assert.Null(await _store.GetAsync(Key));
        Assert.Null(await service.GetAsync(Key));
        Assert.DoesNotContain(await service.ListAsync(), q => q.QueryKey == Key);
        Assert.Equal(404, (await service.RevertAsync(Key, "carl@example.com")).StatusCode);

        var audit = await service.GetAuditAsync(Key, 10);
        Assert.Equal(3, audit.Count);
        Assert.Contains(audit, a => a.ChangedBy == "ana@example.com" && a.Version == 1 && !a.Removed);
        Assert.Contains(audit, a => a.ChangedBy == "bob@example.com" && a.Version == 2 && !a.Removed);
        Assert.Contains(audit, a => a.ChangedBy == "carl@example.com" && a.Removed);

        // A deleted custom query can be restored from its history, as a new version.
        var v2 = audit.Single(a => a.ChangedBy == "bob@example.com");
        var (restoreStatus, _) = await service.RestoreAsync(v2.AuditId, "dana@example.com");
        Assert.Equal(200, restoreStatus);
        Assert.Equal(KpiQuery + "\n| where TotalCount > 0", (await _store.GetAsync(Key))!.KqlText);
    }
}
