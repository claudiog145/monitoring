using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Configuration;

/// <summary>
/// Editable, versioned KQL: the override/fallback precedence, the validation that runs on save,
/// and the history that makes a restore possible.
/// </summary>
public class KqlQueryStoreTests
{
    private static readonly KqlQueryTextProvider BuiltIn = new();

    private static (DbKqlQueryTextProvider Provider, InMemoryKqlQueryStore Store) BuildProvider()
    {
        var store = new InMemoryKqlQueryStore();
        return (new DbKqlQueryTextProvider(BuiltIn, store, NullLogger<DbKqlQueryTextProvider>.Instance), store);
    }

    private static KqlQueryAdminService BuildService(InMemoryKqlQueryStore? store = null) =>
        new(store ?? new InMemoryKqlQueryStore(), BuiltIn, new InMemoryTelemetrySourceStore());

    // -----------------------------------------------------------------------------------------
    // Precedence
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAnyStoredRowEveryKeyResolvesToTheBuiltInText()
    {
        var (provider, _) = BuildProvider();

        // The whole fallback contract: an unseeded database must behave like the previous build.
        Assert.Equal(BuiltIn.GetScoutBoardKql("latency"), provider.GetScoutBoardKql("latency"));
        Assert.Equal(BuiltIn.GetAirJourneyKql(), provider.GetAirJourneyKql());
        Assert.Equal(BuiltIn.GetIncidentExceptionsKql(), provider.GetIncidentExceptionsKql());
    }

    [Fact]
    public async Task AStoredRowOverridesTheBuiltInTextForThatKeyOnly()
    {
        var (provider, store) = BuildProvider();
        await store.UpsertAsync(new KqlQueryEntry(KqlQueryKeys.ScoutBoard("latency"), "requests | count"), "claudio");
        provider.Invalidate();

        Assert.Equal("requests | count", provider.GetScoutBoardKql("latency"));
        Assert.Equal(BuiltIn.GetScoutBoardKql("exception"), provider.GetScoutBoardKql("exception"));
    }

    [Fact]
    public async Task ADisabledRowFallsBackToTheBuiltInInsteadOfRunningNothing()
    {
        var (provider, store) = BuildProvider();
        await store.UpsertAsync(
            new KqlQueryEntry(KqlQueryKeys.ScoutBoard("latency"), "requests | count", Enabled: false), "claudio");
        provider.Invalidate();

        Assert.Equal(BuiltIn.GetScoutBoardKql("latency"), provider.GetScoutBoardKql("latency"));
    }

    /// <summary>
    /// Without this the save would appear to work and the previous text would keep running until
    /// the TTL expired, with no way to tell which version produced a given alert.
    /// </summary>
    [Fact]
    public async Task InvalidateMakesASavedQueryTakeEffectImmediately()
    {
        var (provider, store) = BuildProvider();
        _ = provider.GetScoutDrillDownKql(); // warm the snapshot

        await store.UpsertAsync(new KqlQueryEntry(KqlQueryKeys.ScoutDrillDown, "exceptions | take 1"), "claudio");
        provider.Invalidate();

        Assert.Equal("exceptions | take 1", provider.GetScoutDrillDownKql());
    }

    [Fact]
    public async Task RemovingTheOverrideGoesBackToTheBuiltIn()
    {
        var (provider, store) = BuildProvider();
        await store.UpsertAsync(new KqlQueryEntry(KqlQueryKeys.IncidentExceptions, "exceptions | count"), "claudio");
        provider.Invalidate();
        Assert.Equal("exceptions | count", provider.GetIncidentExceptionsKql());

        await store.DeleteAsync(KqlQueryKeys.IncidentExceptions, "claudio");
        provider.Invalidate();

        Assert.Equal(BuiltIn.GetIncidentExceptionsKql(), provider.GetIncidentExceptionsKql());
    }

    [Fact]
    public void AnUnknownKeyStillThrowsTheSameMessageAsBefore()
    {
        var (provider, _) = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(() => provider.GetScoutBoardKql("whatever"));
        Assert.Contains("latency", ex.Message);
    }

    // -----------------------------------------------------------------------------------------
    // Validation on save
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The placeholder is what scopes the query. Dropping one leaves text that parses perfectly
    /// and answers about the wrong window, which is why this is refused at save time.
    /// </summary>
    [Fact]
    public async Task RefusesToSaveAQueryThatLostARequiredPlaceholder()
    {
        var service = BuildService();

        var error = await service.ValidateAsync(KqlQueryKeys.ScoutBoard("latency"), "requests | count");

        Assert.NotNull(error);
        Assert.Contains("{{TIME_START}}", error);
    }

    [Fact]
    public async Task RefusesToSaveAQueryCarryingALiteralSubscriptionPath()
    {
        var service = BuildService();

        var error = await service.ValidateAsync(
            KqlQueryKeys.IncidentExceptions,
            "app('/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/x/providers/microsoft.insights/components/y').exceptions");

        Assert.NotNull(error);
        Assert.Contains("{{SOURCE:alias}}", error);
    }

    [Fact]
    public async Task RefusesToSaveAQueryPointingAtAnUnregisteredSource()
    {
        var service = BuildService();

        var error = await service.ValidateAsync(KqlQueryKeys.IncidentExceptions, "app('{{SOURCE:nope}}').exceptions");

        Assert.NotNull(error);
        Assert.Contains("nope", error);
    }

    [Fact]
    public async Task RefusesEmptyTextAndUnknownKeys()
    {
        var service = BuildService();

        Assert.NotNull(await service.ValidateAsync(KqlQueryKeys.IncidentExceptions, "   "));
        Assert.NotNull(await service.ValidateAsync("not:a:key", "requests | count"));
    }

    [Fact]
    public async Task AcceptsAValidQueryThatKeepsItsPlaceholdersAndUsesARegisteredAlias()
    {
        var service = BuildService();

        var error = await service.ValidateAsync(
            KqlQueryKeys.ScoutDrillDown,
            "app('{{SOURCE:cruise-core}}').requests | where operation_Id in ({{OPERATION_IDS}}) " +
            "| where timestamp between ({{TIME_START}} .. {{TIME_END}}) | take {{MAX_ROWS}}");

        Assert.Null(error);
    }

    // -----------------------------------------------------------------------------------------
    // History and restore
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task EachSaveIsANewVersionAndTheFullTextOfEveryVersionIsKept()
    {
        var store = new InMemoryKqlQueryStore();
        var service = BuildService(store);
        var key = KqlQueryKeys.IncidentExceptions;

        await service.SaveAsync(key, "exceptions | take 1", "first", null, true, "claudio");
        await service.SaveAsync(key, "exceptions | take 2", "second", null, true, "claudio");

        var history = await store.GetAuditAsync(key, 10);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].Version);

        var oldest = await store.GetAuditEntryAsync(history[1].AuditId);
        Assert.Equal("exceptions | take 1", oldest!.KqlText);
    }

    /// <summary>
    /// Restoring copies the old text forward as a new version instead of rewinding the counter -
    /// the history has to stay append-only or "what was running at 03:00" stops being answerable.
    /// </summary>
    [Fact]
    public async Task RestoringAVersionCopiesItForwardRatherThanRewinding()
    {
        var store = new InMemoryKqlQueryStore();
        var service = BuildService(store);
        var key = KqlQueryKeys.IncidentExceptions;

        await service.SaveAsync(key, "exceptions | take 1", "first", null, true, "claudio");
        await service.SaveAsync(key, "exceptions | take 2", "second", null, true, "claudio");

        var first = (await store.GetAuditAsync(key, 10)).Single(e => e.Version == 1);
        var (status, _) = await service.RestoreAsync(first.AuditId, "claudio");

        Assert.Equal(200, status);
        var current = await store.GetAsync(key);
        Assert.Equal("exceptions | take 1", current!.KqlText);
        Assert.Equal(3, current.Version);
    }

    [Fact]
    public async Task RestoringARemovalEntryIsRefusedBecauseItHasNoText()
    {
        var store = new InMemoryKqlQueryStore();
        var service = BuildService(store);
        var key = KqlQueryKeys.IncidentExceptions;

        await service.SaveAsync(key, "exceptions | take 1", null, null, true, "claudio");
        await service.RevertAsync(key, "claudio");

        var removal = (await store.GetAuditAsync(key, 10)).First(e => e.Removed);
        var (status, _) = await service.RestoreAsync(removal.AuditId, "claudio");

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task EveryChangeRecordsWhoMadeIt()
    {
        var store = new InMemoryKqlQueryStore();
        var service = BuildService(store);

        await service.SaveAsync(KqlQueryKeys.IncidentExceptions, "exceptions | take 1", null, null, true, "claudio");
        await service.RevertAsync(KqlQueryKeys.IncidentExceptions, "claudio");

        var history = await store.GetAuditAsync(null, 10);
        Assert.Equal(2, history.Count);
        Assert.All(history, e => Assert.Equal("claudio", e.ChangedBy));
    }

    // -----------------------------------------------------------------------------------------
    // The admin list and importing the built-ins
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Driven by the built-in key list, not by the table: otherwise the screen would only show
    /// queries somebody had already edited, and the rest would look like they do not exist.
    /// </summary>
    [Fact]
    public async Task TheListShowsEveryKnownKeyEvenWithAnEmptyTable()
    {
        var service = BuildService();

        var list = await service.ListAsync();

        Assert.Equal(BuiltIn.KnownKeys.Count, list.Count);
        Assert.All(list, q => Assert.False(q.HasOverride));
        Assert.Contains(list, q => q.QueryKey == KqlQueryKeys.ScoutBoard("latency"));
    }

    [Fact]
    public async Task ImportingTheBuiltInsSeedsEveryKeyAndSkipsWhatIsAlreadyThere()
    {
        var store = new InMemoryKqlQueryStore();
        var service = BuildService(store);

        var (status, _) = await service.ImportBuiltInsAsync(overwrite: false, "claudio");
        Assert.Equal(200, status);
        Assert.Equal(BuiltIn.KnownKeys.Count, (await store.GetAllAsync()).Count);

        // Second pass must be a no-op rather than bumping every version.
        await service.ImportBuiltInsAsync(overwrite: false, "claudio");
        Assert.All(await store.GetAllAsync(), q => Assert.Equal(1, q.Version));
    }

    [Fact]
    public async Task ImportedBuiltInsPassTheirOwnValidation()
    {
        var service = BuildService();

        // Round-trips the real shipped text through the same rules a human edit has to satisfy -
        // if a built-in query could not be saved, "import built-ins" would be a trap.
        foreach (var key in BuiltIn.KnownKeys)
        {
            Assert.Null(await service.ValidateAsync(key, BuiltIn.GetByKey(key)));
        }
    }

    [Fact]
    public async Task TheDetailViewExposesWhatMustSurviveAnEditAndWhichSourcesAreUsed()
    {
        var service = BuildService();

        var detail = await service.GetAsync(KqlQueryKeys.InspectorLogs("club-baseline"));

        Assert.NotNull(detail);
        Assert.False(detail!.HasOverride);
        Assert.Contains("{{ENTITY_KEY_B64}}", detail.RequiredPlaceholders);
        Assert.Contains("cruise-core", detail.SourceAliases);
        Assert.Equal(detail.BuiltInText, detail.KqlText);
    }

    [Fact]
    public async Task TheDetailViewReturnsNullForAnUnknownKey() =>
        Assert.Null(await BuildService().GetAsync("not:a:key"));
}
