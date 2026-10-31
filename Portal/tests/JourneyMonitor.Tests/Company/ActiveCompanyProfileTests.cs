using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// The company being monitored is a ROW, not an environment variable - so it can be changed while
/// the platform keeps running.
///
/// Every test below drives <see cref="CompanyAdapterProvider"/> through callers that name no
/// profile, because that is the only path the active-company pointer is allowed to touch: a
/// caller that names a company must keep getting the one it named, which is what the whole of
/// ProfileDrivenAdapterTests already depends on.
/// </summary>
public sealed class ActiveCompanyProfileTests
{
    private static TelemetryRowContext Row() => new(
        Url: "https://edge.example.test/api/v2/checkout",
        Operation: "POST /api/v2/checkout");

    // ---------- precedence ----------

    [Fact]
    public async Task GetAsync_WithNoKey_PrefersTheActiveCompanyOverTheConfiguredDefault()
    {
        var provider = Provider(new FakeActiveStore("profile-b"), defaultProfileKey: "profile-a");

        var adapter = await provider.GetAsync();

        Assert.Equal("profile-b", adapter.ProfileKey);
    }

    [Fact]
    public async Task GetAsync_WithAnExplicitKey_BeatsTheActiveCompany()
    {
        var provider = Provider(new FakeActiveStore("profile-b"), defaultProfileKey: "profile-a");

        var adapter = await provider.GetAsync("profile-a");

        Assert.Equal("profile-a", adapter.ProfileKey);
    }

    [Fact]
    public async Task GetAsync_WithAnExplicitKey_NeverReadsTheActiveCompany()
    {
        // Not just the same answer - the same WORK. An explicit key is the scout path, and a
        // round trip per board would be a cost this feature has no business adding there.
        var active = new FakeActiveStore("profile-b");
        var provider = Provider(active, defaultProfileKey: "profile-a");

        await provider.GetAsync("profile-a");

        Assert.Equal(0, active.GetCalls);
    }

    [Fact]
    public async Task GetAsync_WithNothingActive_FallsBackToTheConfiguredDefault()
    {
        var provider = Provider(new FakeActiveStore(null), defaultProfileKey: "profile-a");

        var adapter = await provider.GetAsync();

        Assert.Equal("profile-a", adapter.ProfileKey);
    }

    [Fact]
    public async Task GetAsync_WithNoActiveStoreAtAll_FallsBackToTheConfiguredDefault()
    {
        // The shape of a deployment whose database predates monitoring.ActiveCompanyProfile: the
        // switch is simply not offered, and behaviour is exactly what shipped before it existed.
        var provider = Provider(activeStore: null, defaultProfileKey: "profile-a");

        var adapter = await provider.GetAsync();

        Assert.Equal("profile-a", adapter.ProfileKey);
    }

    [Fact]
    public async Task GetAsync_WithNothingActiveAndNoDefault_Throws()
    {
        var provider = Provider(new FakeActiveStore(null), defaultProfileKey: "");

        var ex = await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => provider.GetAsync());

        // The message is the only thing an operator sees, and it has to name the way out.
        Assert.Contains("company-profiles-active", ex.Message);
    }

    // ---------- a read failure is a degraded preference, not an outage ----------

    [Fact]
    public async Task GetAsync_WhenTheActiveStoreFaults_FallsBackToTheConfiguredDefault()
    {
        var active = new FakeActiveStore("profile-b") { Fault = new InvalidOperationException("pointer unreadable") };
        var provider = Provider(active, defaultProfileKey: "profile-a");

        var adapter = await provider.GetAsync();

        Assert.Equal("profile-a", adapter.ProfileKey);
    }

    [Fact]
    public async Task GetAsync_AfterAFailedActiveRead_RetriesRatherThanServingTheFallbackForTheWholeTtl()
    {
        var active = new FakeActiveStore("profile-b") { Fault = new InvalidOperationException("transient") };
        var provider = Provider(active, defaultProfileKey: "profile-a");

        Assert.Equal("profile-a", (await provider.GetAsync()).ProfileKey);

        active.Fault = null;

        // No Invalidate(): a cached failure would outlive the fix that repaired it.
        Assert.Equal("profile-b", (await provider.GetAsync()).ProfileKey);
    }

    // ---------- caching ----------

    [Fact]
    public async Task GetAsync_CachesTheActiveCompanyUntilInvalidate()
    {
        var active = new FakeActiveStore("profile-a");
        var provider = Provider(active, defaultProfileKey: "profile-a");

        await provider.GetAsync();
        await provider.GetAsync();
        Assert.Equal(1, active.GetCalls);

        provider.Invalidate();
        await provider.GetAsync();
        Assert.Equal(2, active.GetCalls);
    }

    [Fact]
    public async Task GetAsync_CachesTheAbsenceOfAnActiveCompanyToo()
    {
        // "Nobody has chosen" is a successful read. Leaving it uncached would put a round trip in
        // front of every classification on a deployment that never activates anything.
        var active = new FakeActiveStore(null);
        var provider = Provider(active, defaultProfileKey: "profile-a");

        await provider.GetAsync();
        await provider.GetAsync();

        Assert.Equal(1, active.GetCalls);
    }

    // ---------- the requirement itself ----------

    [Fact]
    public async Task SwitchingTheActiveCompany_ChangesWhatUnnamedCallersClassifyWith()
    {
        // One live provider, no restart, no configuration change: the same row comes back as two
        // different companies' vocabulary purely because the pointer moved.
        var active = new FakeActiveStore("profile-a");
        var provider = Provider(active, defaultProfileKey: "profile-a");

        var before = await provider.GetAsync();

        await active.SetActiveAsync("profile-b", "operator");
        provider.Invalidate();

        var after = await provider.GetAsync();

        Assert.Equal("profile-a", before.ProfileKey);
        Assert.Equal("profile-b", after.ProfileKey);
        Assert.Equal("freight", before.ResolveStep(Row()).Service);
        Assert.Equal("playback", after.ResolveStep(Row()).Service);
    }

    [Fact]
    public async Task SwitchingTheActiveCompany_WithoutInvalidate_IsNotSeenImmediately()
    {
        // Pins why CompanyProfileAdminFunctions invalidates after the write instead of trusting
        // the TTL: without it the platform keeps classifying against the company nobody selected.
        var active = new FakeActiveStore("profile-a");
        var provider = Provider(active, defaultProfileKey: "profile-a");

        await provider.GetAsync();
        await active.SetActiveAsync("profile-b", "operator");

        Assert.Equal("profile-a", (await provider.GetAsync()).ProfileKey);
    }

    // ---------- fixtures ----------

    private static CompanyAdapterProvider Provider(IActiveCompanyProfileStore? activeStore, string defaultProfileKey) =>
        new(new FakeProfileStore(ProfileA(), ProfileB()),
            Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = defaultProfileKey }),
            NullLogger<CompanyAdapterProvider>.Instance,
            activeStore);

    /// <summary>A freight company. No host filters: which company answers is the only variable here.</summary>
    private static CompanyProfile ProfileA() => new(
        ProfileKey: "profile-a",
        DisplayName: "Meridian Freight",
        Labels: new CompanyLabels("Lane", "Waterfall", "Checkpoint", "Shipper", "Crew"),
        Services:
        [
            new ServiceDefinition("freight", "Freight", [], [],
            [
                new FlowDefinition("dispatch", "Dispatch", [
                    new StepDefinition("label-printed", "Label printed", 10, [
                        new StepMatcher(StepMatcherKind.UrlContains, "/api/v2/checkout"),
                    ]),
                ]),
            ]),
        ],
        EntityIdentitySources: [],
        Glossary: new CompanyGlossary("lane", "waterfall", "checkpoint", "shipper", "crew"));

    /// <summary>A streaming company, given the identical row and expected to read it differently.</summary>
    private static CompanyProfile ProfileB() => new(
        ProfileKey: "profile-b",
        DisplayName: "Helios Streaming",
        Labels: new CompanyLabels("Channel", "Session", "Beat", "Subscriber", "Squad"),
        Services:
        [
            new ServiceDefinition("playback", "Playback", [], [],
            [
                new FlowDefinition("viewing", "Viewing", [
                    new StepDefinition("stream-started", "Stream started", 10, [
                        new StepMatcher(StepMatcherKind.OperationContains, "checkout"),
                    ]),
                ]),
            ]),
        ],
        EntityIdentitySources: [],
        Glossary: new CompanyGlossary("channel", "session", "beat", "subscriber", "squad"));

    private sealed class FakeProfileStore(params CompanyProfile[] profiles) : ICompanyProfileStore
    {
        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default) =>
            Task.FromResult<CompanyProfile?>(profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase) && p.Enabled));

        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfile>>(profiles);

        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default) =>
            Task.FromResult(profile);

        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfileAuditEntry>>([]);
    }

    /// <summary>Counts reads, so "cached" and "re-read after Invalidate" are observable rather than inferred.</summary>
    private sealed class FakeActiveStore(string? profileKey) : IActiveCompanyProfileStore
    {
        private string? _profileKey = profileKey;

        public int GetCalls { get; private set; }

        public Exception? Fault { get; set; }

        public Task<string?> GetActiveAsync(CancellationToken ct = default)
        {
            GetCalls++;
            if (Fault is not null) throw Fault;
            return Task.FromResult<string?>(_profileKey);
        }

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
            Task.FromResult<ActiveCompanyProfile?>(_profileKey is null
                ? null
                : new ActiveCompanyProfile(_profileKey, DateTimeOffset.UtcNow, "test"));

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default)
        {
            _profileKey = profileKey;
            return Task.CompletedTask;
        }
    }
}
