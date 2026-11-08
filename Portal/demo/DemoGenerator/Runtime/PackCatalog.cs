using DemoGenerator.Archetypes;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Runtime;

/// <summary>The company packs found under <see cref="DemoOptions.PacksPath"/>. Invalid packs are skipped and reported in <see cref="Errors"/>.</summary>
public sealed class PackCatalog(ILogger<PackCatalog> logger)
{
    private volatile Snapshot _snapshot = new(new Dictionary<string, Archetype>(StringComparer.Ordinal), new Dictionary<string, SessionFlows>(StringComparer.Ordinal), []);

    private sealed record Snapshot(IReadOnlyDictionary<string, Archetype> Companies, IReadOnlyDictionary<string, SessionFlows> Sessions, IReadOnlyList<string> Errors);

    public IReadOnlyDictionary<string, Archetype> Companies => _snapshot.Companies;

    public IReadOnlyList<string> Errors => _snapshot.Errors;

    public Archetype? Find(string companyKey) => _snapshot.Companies.GetValueOrDefault(companyKey);

    /// <summary>The flow positions of the company's steps from its <c>profile.json</c>; null when the company is unknown.</summary>
    public SessionFlows? SessionsOf(string companyKey) => _snapshot.Sessions.GetValueOrDefault(companyKey);

    /// <summary>The company's plan with its sessions.</summary>
    public CompanyPlan Plan(Archetype archetype, string scale, long seed) =>
        CompanyPlan.Build(archetype, scale, seed, SessionsOf(archetype.CompanyKey!));

    public void Load(string packsPath)
    {
        var companies = new Dictionary<string, Archetype>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, SessionFlows>(StringComparer.Ordinal);
        var errors = new List<string>();
        var root = Path.GetFullPath(packsPath);
        if (!Directory.Exists(root))
        {
            errors.Add($"Packs folder '{root}' does not exist.");
        }
        else
        {
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(dir, "archetype.json");
                if (!File.Exists(file)) continue;
                var (archetype, problems) = ArchetypeLoader.TryLoad(file);
                if (archetype is null)
                {
                    errors.AddRange(problems.Select(p => $"{Path.GetFileName(dir)}: {p}"));
                    continue;
                }
                if (!companies.TryAdd(archetype.CompanyKey!, archetype))
                {
                    errors.Add($"{Path.GetFileName(dir)}: companyKey '{archetype.CompanyKey}' is already used by another pack.");
                    continue;
                }
                var (flows, problem) = SessionFlows.Load(dir, archetype);
                if (problem is not null) logger.LogWarning("Pack {Pack}: {Problem}", Path.GetFileName(dir), problem);
                sessions[archetype.CompanyKey!] = flows;
            }
        }

        foreach (var error in errors) logger.LogError("Pack skipped: {Error}", error);
        logger.LogInformation("Loaded {Count} company pack(s) from {Path}: {Keys}", companies.Count, root, string.Join(", ", companies.Keys));
        _snapshot = new Snapshot(companies, sessions, errors);
    }
}
