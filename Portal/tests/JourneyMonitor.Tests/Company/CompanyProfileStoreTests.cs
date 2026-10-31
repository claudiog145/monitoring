using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// Covers the behaviour every <see cref="ICompanyProfileStore"/> implementation owes its callers,
/// exercised against the in-memory store. The SQL store implements the same contract in T-SQL
/// (usp_UpsertCompanyProfile / usp_DeleteCompanyProfile), so these are the assertions the
/// integrator should re-run against a provisioned database.
/// </summary>
public class CompanyProfileStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CompanyProfile Demo(string key = "demo", bool enabled = true) =>
        new(
            key,
            "Demo Company",
            new CompanyLabels("Product area", "Journey", "Step", "Tenant", "Owning team"),
            [
                new ServiceDefinition(
                    "checkout",
                    "Checkout",
                    ["club"],
                    [],
                    [
                        new FlowDefinition("purchase", "Purchase",
                        [
                            new StepDefinition("step-01", "Step 01", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/search")]),
                            new StepDefinition("step-02", "Step 02", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/details")]),
                            new StepDefinition("step-03", "Step 03", 3, [new StepMatcher(StepMatcherKind.UrlContains, "/checkout")]),
                            new StepDefinition("step-04", "Step 04", 4, [new StepMatcher(StepMatcherKind.UrlContains, "/confirmation")])
                        ])
                    ])
            ],
            [
                new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "VacationClubId", "tenant"),
                new EntityIdentitySource(2, EntityIdentitySourceKind.CustomDimension, "IcePartnerId", "tenant")
            ],
            new CompanyGlossary("product area", "journey", "step", "tenant", "owning team", "A four-step checkout."),
            Enabled: enabled);

    [Fact]
    public async Task Upsert_then_Get_round_trips_the_whole_document()
    {
        var store = new InMemoryCompanyProfileStore();

        await store.UpsertAsync(Demo(), "claudio");
        var loaded = await store.GetAsync("demo");

        Assert.NotNull(loaded);
        Assert.Equal("demo", loaded!.ProfileKey);
        Assert.Equal("Tenant", loaded.Labels.Entity);
        Assert.Equal("Step", loaded.Labels.Step);
        Assert.Equal("claudio", loaded.UpdatedBy);

        var service = loaded.FindService("checkout");
        Assert.NotNull(service);
        Assert.Equal("Checkout", service!.DisplayName);

        var steps = service.Flows.Single().Steps;
        Assert.Equal(new[] { "step-01", "step-02", "step-03", "step-04" }, steps.Select(s => s.Key));
        Assert.Equal(new[] { 1, 2, 3, 4 }, steps.Select(s => s.Order));
        Assert.Equal("tenant", loaded.EntityIdentitySources[0].EntityKind);
    }

    [Fact]
    public async Task Upsert_starts_at_version_one_and_bumps_on_every_write()
    {
        var store = new InMemoryCompanyProfileStore();

        var first = await store.UpsertAsync(Demo(), "claudio");
        Assert.Equal(1, first.Version);

        // The submitted Version is ignored - the store owns it, so a stale client cannot rewind
        // the history by posting an old document back.
        var second = await store.UpsertAsync(Demo() with { Version = 99 }, "claudio");
        Assert.Equal(2, second.Version);
        Assert.Equal(2, (await store.GetAsync("demo"))!.Version);
    }

    [Fact]
    public async Task Audit_captures_the_full_document_of_every_revision()
    {
        var store = new InMemoryCompanyProfileStore();

        await store.UpsertAsync(Demo(), "claudio");
        await store.UpsertAsync(Demo() with { DisplayName = "Renamed" }, "someone-else");

        var audit = await store.GetAuditAsync(10);
        Assert.Equal(2, audit.Count);

        var newest = audit[0];
        Assert.Equal(2, newest.Version);
        Assert.Equal("someone-else", newest.ChangedBy);
        Assert.False(newest.Removed);

        // The point of the full-document audit: restoring a revision must be a copy, not a
        // reconstruction, so the stored JSON has to deserialise straight back into a profile.
        var restored = JsonSerializer.Deserialize<CompanyProfile>(newest.ProfileJson, JsonOptions);
        Assert.NotNull(restored);
        Assert.Equal("Renamed", restored!.DisplayName);
        Assert.Equal("Checkout", restored.FindService("checkout")!.DisplayName);
        Assert.Equal(4, restored.FindService("checkout")!.Flows.Single().Steps.Count);

        // camelCase, because the SQL column is read back by property name. PascalCase here is the
        // bug that once made the whole UI read null.
        Assert.Contains("\"profileKey\"", newest.ProfileJson);
        Assert.Contains("\"entityIdentitySources\"", newest.ProfileJson);
        Assert.DoesNotContain("\"ProfileKey\"", newest.ProfileJson);

        var oldest = audit[1];
        Assert.Equal(1, oldest.Version);
        Assert.Equal("Demo Company", JsonSerializer.Deserialize<CompanyProfile>(oldest.ProfileJson, JsonOptions)!.DisplayName);
    }

    [Fact]
    public async Task Delete_removes_the_row_and_records_Removed_with_the_last_known_document()
    {
        var store = new InMemoryCompanyProfileStore();
        await store.UpsertAsync(Demo(), "claudio");

        await store.DeleteAsync("demo", "offboarding");

        Assert.Null(await store.GetAsync("demo"));
        Assert.Empty(await store.GetAllAsync());

        var newest = (await store.GetAuditAsync(10))[0];
        Assert.True(newest.Removed);
        Assert.Equal("offboarding", newest.ChangedBy);
        Assert.Equal(1, newest.Version);
        // Off-boarding must not destroy the mapping that explains the alerts already raised.
        Assert.Equal("Demo Company", JsonSerializer.Deserialize<CompanyProfile>(newest.ProfileJson, JsonOptions)!.DisplayName);
    }

    [Fact]
    public async Task Delete_of_an_unknown_key_is_still_recorded()
    {
        var store = new InMemoryCompanyProfileStore();

        await store.DeleteAsync("never-existed", "someone");

        var newest = (await store.GetAuditAsync(10))[0];
        Assert.True(newest.Removed);
        Assert.Equal("never-existed", newest.ProfileKey);
    }

    [Fact]
    public async Task Disabled_profile_is_not_returned_by_GetAsync_but_is_still_listed()
    {
        var store = new InMemoryCompanyProfileStore();
        await store.UpsertAsync(Demo(enabled: false), "claudio");

        // Switched off means it must not classify traffic...
        Assert.Null(await store.GetAsync("demo"));

        // ...but it still has to be visible to whoever has to switch it back on.
        var all = await store.GetAllAsync();
        Assert.Equal("demo", Assert.Single(all).ProfileKey);
        Assert.False(all[0].Enabled);
    }

    [Fact]
    public async Task Seeded_profiles_are_readable_without_a_write()
    {
        var store = new InMemoryCompanyProfileStore([Demo("arrivia")]);

        Assert.NotNull(await store.GetAsync("arrivia"));
        Assert.Null(await store.GetAsync("demo"));
    }

    [Fact]
    public async Task Keys_are_matched_case_insensitively_like_the_SQL_collation()
    {
        var store = new InMemoryCompanyProfileStore();
        await store.UpsertAsync(Demo("Demo"), "claudio");

        Assert.NotNull(await store.GetAsync("demo"));
        // Not two companies: the SQL primary key is case-insensitive under the default collation,
        // and a store that disagreed would onboard a duplicate that only exists in one of them.
        await store.UpsertAsync(Demo("DEMO"), "claudio");
        Assert.Single(await store.GetAllAsync());
    }
}
