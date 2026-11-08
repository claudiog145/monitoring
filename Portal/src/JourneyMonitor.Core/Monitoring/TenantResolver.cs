using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// One telemetry tenant as declared by a company profile (<see cref="CompanyProfile.TelemetryTenants"/>).
    /// </summary>
    public class AppInsightsTenant
    {
        /// <summary>Key the tenant's App Insights token is stored under.</summary>
        public string TenantKey { get; set; } = "";

        /// <summary>Azure Subscription ID where AppInsights is located. Not declared by the profile today.</summary>
        public string? SubscriptionId { get; set; }

        /// <summary>Azure Resource Group containing AppInsights. Not declared by the profile today.</summary>
        public string? ResourceGroup { get; set; }

        /// <summary>Name of Application Insights resource. Not declared by the profile today.</summary>
        public string? AppInsightsName { get; set; }

        /// <summary>Azure AD (Entra) tenant ID.</summary>
        public string AadTenantId { get; set; } = "";

        /// <summary>Telemetry domains (InsightsOptions slot names, lower case) this key answers for.</summary>
        public IReadOnlyList<string> Domains { get; set; } = [];

        /// <summary>
        /// Full resource ID for Azure API calls
        /// Format: /subscriptions/{subId}/resourceGroups/{rg}/providers/microsoft.insights/components/{name}
        /// </summary>
        public string GetResourceId()
        {
            return $"/subscriptions/{SubscriptionId}" +
                   $"/resourceGroups/{ResourceGroup}" +
                   $"/providers/microsoft.insights/components/{AppInsightsName}";
        }
    }

    /// <summary>
    /// Maps telemetry tenants to the keys their App Insights tokens are stored under. Backed by the
    /// <see cref="CompanyProfile.TelemetryTenants"/> of the registered company profiles.
    /// </summary>
    public interface ITenantResolver
    {
        /// <summary>Configuration for a tenant key; throws <see cref="KeyNotFoundException"/> when no profile declares it.</summary>
        Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey);

        /// <summary>Every declared tenant, active profile first (for UI dropdowns).</summary>
        Task<List<AppInsightsTenant>> GetAllTenantsAsync();

        /// <summary>
        /// The stored-token key for an Entra tenant UUID, optionally narrowed by telemetry domain.
        /// Returns the UUID itself when no profile declares the tenant.
        /// </summary>
        Task<string> ResolveTenantKeyAsync(string tenantId, string? domain = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Profile-driven <see cref="ITenantResolver"/>. Reads the profiles through
    /// <see cref="ICompanyProfileStore"/> and keeps them for <see cref="CacheTtl"/>, active profile
    /// first. A read failure is logged and not cached; resolution then degrades to "the UUID is its
    /// own key", which is the same answer an undeclared tenant gets.
    /// </summary>
    public class TenantResolver : ITenantResolver
    {
        public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private readonly ICompanyProfileStore? _store;
        private readonly IActiveCompanyProfileStore? _activeStore;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private volatile Snapshot? _snapshot;

        /// <summary>No profile store: nothing is declared, every tenant resolves to its own UUID.</summary>
        public TenantResolver()
            : this(store: null)
        {
        }

        public TenantResolver(
            ICompanyProfileStore? store,
            IActiveCompanyProfileStore? activeStore = null,
            ILogger<TenantResolver>? logger = null,
            TimeProvider? time = null)
        {
            _store = store;
            _activeStore = activeStore;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
            _time = time ?? TimeProvider.System;
        }

        public async Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                throw new ArgumentException("Tenant key is required", nameof(tenantKey));

            var tenants = await GetAllTenantsAsync();
            var match = tenants.FirstOrDefault(t => string.Equals(t.TenantKey, tenantKey.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new KeyNotFoundException(
                $"Tenant configuration not found for key: {tenantKey}. " +
                $"Available tenants: {string.Join(", ", tenants.Select(t => t.TenantKey))}");
        }

        public async Task<List<AppInsightsTenant>> GetAllTenantsAsync()
        {
            var snapshot = await GetSnapshotAsync(CancellationToken.None);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AppInsightsTenant>();
            foreach (var tenant in snapshot.Profiles.SelectMany(Tenants))
            {
                if (!seen.Add(tenant.TenantKey.Trim())) continue;
                result.Add(new AppInsightsTenant
                {
                    TenantKey = tenant.TenantKey.Trim(),
                    AadTenantId = tenant.AadTenantId?.Trim() ?? "",
                    Domains = tenant.Domains ?? [],
                });
            }
            return result;
        }

        public async Task<string> ResolveTenantKeyAsync(string tenantId, string? domain = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return tenantId ?? string.Empty;

            var snapshot = await GetSnapshotAsync(ct);
            return Resolve(snapshot.Profiles, snapshot.ActiveProfileKey, tenantId, domain);
        }

        /// <summary>
        /// Pure resolution: the active profile is consulted first and the rest only when it does not
        /// declare the tenant. Within the chosen candidates the one listing <paramref name="domain"/>
        /// wins, otherwise the first in profile order. No match returns <paramref name="tenantId"/>.
        /// </summary>
        public static string Resolve(
            IReadOnlyList<CompanyProfile> profiles, string? activeProfileKey, string tenantId, string? domain)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return tenantId ?? string.Empty;
            var uuid = tenantId.Trim();

            var ordered = Order(profiles, activeProfileKey);
            var active = ordered.FirstOrDefault(p => IsActive(p, activeProfileKey));

            List<TelemetryTenant> candidates = active is null ? new() : Candidates([active], uuid);
            if (candidates.Count == 0) candidates = Candidates(ordered, uuid);
            if (candidates.Count == 0) return tenantId;

            if (!string.IsNullOrWhiteSpace(domain))
            {
                var wanted = domain.Trim();
                var byDomain = candidates.FirstOrDefault(t =>
                    (t.Domains ?? []).Any(d => string.Equals(d?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)));
                if (byDomain is not null) return byDomain.TenantKey.Trim();
            }

            return candidates[0].TenantKey.Trim();
        }

        /// <summary>Drops the cached profiles so the next call reads the store again.</summary>
        public void Invalidate() => _snapshot = null;

        private static List<TelemetryTenant> Candidates(IEnumerable<CompanyProfile> profiles, string uuid) =>
            profiles.SelectMany(Tenants)
                .Where(t => string.Equals(t.AadTenantId?.Trim(), uuid, StringComparison.OrdinalIgnoreCase))
                .ToList();

        private static IEnumerable<TelemetryTenant> Tenants(CompanyProfile profile) =>
            (profile.TelemetryTenants ?? []).Where(t => t is not null && !string.IsNullOrWhiteSpace(t.TenantKey));

        private static bool IsActive(CompanyProfile profile, string? activeProfileKey) =>
            !string.IsNullOrWhiteSpace(activeProfileKey)
            && string.Equals(profile.ProfileKey, activeProfileKey.Trim(), StringComparison.OrdinalIgnoreCase);

        private static List<CompanyProfile> Order(IReadOnlyList<CompanyProfile> profiles, string? activeProfileKey) =>
            (profiles ?? [])
                .Where(p => p is not null && p.Enabled)
                .OrderBy(p => IsActive(p, activeProfileKey) ? 0 : 1)
                .ToList();

        private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
        {
            var now = _time.GetUtcNow();
            var cached = _snapshot;
            if (cached is not null && cached.ExpiresAt > now) return cached;
            if (_store is null) return _snapshot = new Snapshot([], null, DateTimeOffset.MaxValue);

            await _loadLock.WaitAsync(ct);
            try
            {
                cached = _snapshot;
                if (cached is not null && cached.ExpiresAt > _time.GetUtcNow()) return cached;

                IReadOnlyList<CompanyProfile> profiles;
                try
                {
                    profiles = await _store.GetAllAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TenantResolver] company profiles could not be read; tenants resolve to their own id until the next read.");
                    return new Snapshot([], null, DateTimeOffset.MinValue);
                }

                string? activeKey = null;
                if (_activeStore is not null)
                {
                    try
                    {
                        activeKey = (await _activeStore.GetActiveAsync(ct))?.Trim();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[TenantResolver] the active company profile could not be read; resolving against every profile.");
                    }
                }

                var ordered = Order(profiles, activeKey);
                var snapshot = new Snapshot(ordered, activeKey, _time.GetUtcNow().Add(CacheTtl));
                _snapshot = snapshot;
                return snapshot;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private sealed record Snapshot(IReadOnlyList<CompanyProfile> Profiles, string? ActiveProfileKey, DateTimeOffset ExpiresAt);
    }
}
