using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// "The database wins, configuration is the fallback" decided in one place. Every case here is a
/// spend limit, so getting the precedence wrong is not a cosmetic bug.
/// </summary>
public class AgentBudgetResolverTests
{
    private static InspectorOptions Defaults() => new()
    {
        DebateModels = "model-a,model-b,model-c",
        DebateRounds = 2,
        MaxHelpHops = 3,
        MaxInvestigationsPerDay = 40,
        SkipDebateWhenContributorsBelow = null,
    };

    private static AgentBudgetResolver Build(
        IAgentBudgetStore store,
        bool storeConfigured = true,
        InspectorOptions? options = null) =>
        new(store,
            Options.Create(new JourneyStoreOptions { ConnectionString = storeConfigured ? "Server=test;" : null }),
            Options.Create(options ?? Defaults()),
            NullLogger<AgentBudgetResolver>.Instance);

    private static AgentBudgetSetting Inspector(
        int? maxRunsPerDay = null,
        string? debateModels = null,
        int? debateRounds = null,
        int? maxHelpHops = null,
        int? skipBelow = null) =>
        new(MonkeyKey.InspectorMonkey, maxRunsPerDay, debateModels, debateRounds, maxHelpHops, skipBelow,
            DateTimeOffset.UtcNow, "test");

    [Fact]
    public async Task WithNothingStoredEveryDialComesFromConfiguration()
    {
        var budget = await Build(new InMemoryAgentBudgetStore()).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(["model-a", "model-b", "model-c"], budget.DebateModels);
        Assert.Equal(2, budget.DebateRounds);
        Assert.Equal(3, budget.MaxHelpHops);
        Assert.Equal(40, budget.MaxInvestigationsPerDay);
        Assert.Equal(0, budget.SkipDebateWhenContributorsBelow);
    }

    [Fact]
    public async Task AStoredValueWins()
    {
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(Inspector(maxRunsPerDay: 5, debateRounds: 1, skipBelow: 3), "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(5, budget.MaxInvestigationsPerDay);
        Assert.Equal(1, budget.DebateRounds);
        Assert.Equal(3, budget.SkipDebateWhenContributorsBelow);
    }

    [Fact]
    public async Task ANullFieldFallsBackPerFieldRatherThanPinningTheWholeRow()
    {
        // Writing one dial must not freeze the other four at whatever the defaults were that day.
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(Inspector(debateRounds: 0), "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(0, budget.DebateRounds);
        Assert.Equal(3, budget.MaxHelpHops);
        Assert.Equal(40, budget.MaxInvestigationsPerDay);
        Assert.Equal(["model-a", "model-b", "model-c"], budget.DebateModels);
    }

    [Fact]
    public async Task ZeroRoundsIsAValueAndNotAnAbsentOne()
    {
        // The options use "is > 0" to mean "unset", which would read a deliberate 0 as "use the
        // default 2" - the single most expensive off-by-one available here.
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(Inspector(debateRounds: 0, maxHelpHops: 0), "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(0, budget.DebateRounds);
        Assert.Equal(0, budget.MaxHelpHops);
    }

    [Fact]
    public async Task AStoredRosterReplacesTheConfiguredOne()
    {
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(Inspector(debateModels: " gpt-4o , claude-opus-5 , gpt-4o "), "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        // Trimmed and de-duplicated: a roster listing the same model twice would pay twice to
        // debate with itself.
        Assert.Equal(["gpt-4o", "claude-opus-5"], budget.DebateModels);
    }

    [Fact]
    public async Task AnEmptyRosterKeepsTheConfiguredOneInsteadOfSilencingTheDebate()
    {
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(Inspector(debateModels: "   ,  ,"), "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(["model-a", "model-b", "model-c"], budget.DebateModels);
    }

    [Fact]
    public async Task AnUnconfiguredStoreResolvesToConfigurationWithoutTouchingIt()
    {
        var store = new ThrowingAgentBudgetStore();

        var budget = await Build(store, storeConfigured: false).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(40, budget.MaxInvestigationsPerDay);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AFailingStoreFallsBackToConfigurationAndNotToUnlimited()
    {
        // The direction matters: a lost connection must never be able to RAISE a spend ceiling.
        var budget = await Build(new ThrowingAgentBudgetStore()).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(40, budget.MaxInvestigationsPerDay);
        Assert.Equal(2, budget.DebateRounds);
    }

    [Fact]
    public async Task TheSnapshotIsCachedAndInvalidateDropsIt()
    {
        var store = new CountingAgentBudgetStore();
        var resolver = Build(store);

        await resolver.ResolveInspectorAsync(CancellationToken.None);
        await resolver.ResolveInspectorAsync(CancellationToken.None);
        Assert.Equal(1, store.Calls);

        resolver.Invalidate();
        await resolver.ResolveInspectorAsync(CancellationToken.None);
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task ARowForAnotherAgentDoesNotLeakIntoTheInspector()
    {
        var store = new InMemoryAgentBudgetStore();
        await store.UpsertAsync(
            new AgentBudgetSetting(MonkeyKey.DoctorMonkey, 1, "only-one-model", 0, 0, 9, DateTimeOffset.UtcNow, "test"),
            "test", CancellationToken.None);

        var budget = await Build(store).ResolveInspectorAsync(CancellationToken.None);

        Assert.Equal(40, budget.MaxInvestigationsPerDay);
        Assert.Equal(["model-a", "model-b", "model-c"], budget.DebateModels);
    }

    private sealed class ThrowingAgentBudgetStore : IAgentBudgetStore
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("the settings database is unreachable");
        }

        public Task<AgentBudgetSetting> UpsertAsync(AgentBudgetSetting setting, string changedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentBudgetSettingAudit>> GetAuditAsync(string monkeyKey, int top, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingAgentBudgetStore : IAgentBudgetStore
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AgentBudgetSetting>>([]);
        }

        public Task<AgentBudgetSetting> UpsertAsync(AgentBudgetSetting setting, string changedBy, CancellationToken cancellationToken) =>
            Task.FromResult(setting);

        public Task<IReadOnlyList<AgentBudgetSettingAudit>> GetAuditAsync(string monkeyKey, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentBudgetSettingAudit>>([]);
    }
}
