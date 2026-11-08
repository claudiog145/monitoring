using System.Text.Json;
using DemoGenerator.Archetypes;
using DemoGenerator.Kusto;
using DemoGenerator.Synthesis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DemoGenerator.Tests;

/// <summary>The test-only pack under Fixtures/minipack and helpers to load or copy it.</summary>
public static class Fixtures
{
    public static string PacksRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PackDir => Path.Combine(PacksRoot, "minipack");

    public static Archetype Archetype() => ArchetypeLoader.Load(Path.Combine(PackDir, "archetype.json"));

    public static string ArchetypeJson() => File.ReadAllText(Path.Combine(PackDir, "archetype.json"));

    public static CompanyPlan Plan(string scale = Scales.Laptop, long seed = 1000) => CompanyPlan.Build(Archetype(), scale, seed);

    public static AppPlan WebApp(CompanyPlan plan) => plan.Apps.Single(a => a.Database == "fixture-web");

    public static AppPlan ApiApp(CompanyPlan plan) => plan.Apps.Single(a => a.Database == "fixture-api");

    /// <summary>A UTC Monday, so weekday and weekend factors are predictable.</summary>
    public static readonly DateTimeOffset Monday = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>A writable copy of the fixture pack under the test output folder, deleted on dispose.</summary>
public sealed class PackCopy : IDisposable
{
    public PackCopy()
    {
        Dir = Path.Combine(AppContext.BaseDirectory, "work", Guid.NewGuid().ToString("N"));
        Copy(Fixtures.PackDir, Dir);
    }

    public string Dir { get; }

    public string Read(string file) => File.ReadAllText(Path.Combine(Dir, file));

    public void Write(string file, string text) => File.WriteAllText(Path.Combine(Dir, file), text);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}

/// <summary>An in-memory Kusto: records every command and answers the few reads the generator makes.</summary>
public sealed class FakeKustoClient : IKustoClient
{
    private readonly object _gate = new();

    public List<(string Database, string Csl)> Commands { get; } = [];

    /// <summary>Commands that <see cref="FailWhen"/> rejected (not in <see cref="Commands"/>).</summary>
    public List<(string Database, string Csl)> Failed { get; } = [];

    public Func<string, string, bool>? FailWhen { get; set; }

    public void ClearLog()
    {
        lock (_gate) { Commands.Clear(); Failed.Clear(); }
    }

    public HashSet<string> Databases { get; } = new(StringComparer.OrdinalIgnoreCase) { "NetDefaultDB" };

    public Task<KustoResult> QueryAsync(string database, string csl, CancellationToken ct = default)
    {
        lock (_gate) Commands.Add((database, csl));
        if (csl.Contains("summarize c = count()", StringComparison.Ordinal))
            return Task.FromResult(Result(["c", "t"], KqlSynthesisBuilder.Tables.Select(t => $"[{(t == "requests" ? 10 : 1)}, \"{t}\"]")));
        return Task.FromResult(KustoResult.Empty);
    }

    public Task<KustoResult> ExecuteCommandAsync(string database, string csl, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (FailWhen?.Invoke(database, csl) == true)
            {
                Failed.Add((database, csl));
                throw new KustoCommandException($"Kusto 520 on {database}: injected failure", 520);
            }
            Commands.Add((database, csl));
            if (csl.StartsWith(".create database ['", StringComparison.Ordinal))
                Databases.Add(csl[".create database ['".Length..csl.IndexOf("']", StringComparison.Ordinal)]);
            if (csl.StartsWith(".show databases", StringComparison.Ordinal))
                return Task.FromResult(Result(["DatabaseName"], Databases.Select(d => $"[\"{d}\"]")));
        }
        return Task.FromResult(KustoResult.Empty);
    }

    public IReadOnlyList<(string Database, string Csl)> Snapshot()
    {
        lock (_gate) return Commands.ToList();
    }

    private static KustoResult Result(IReadOnlyList<string> columns, IEnumerable<string> rows)
    {
        using var doc = JsonDocument.Parse("[" + string.Join(",", rows) + "]");
        return new KustoResult(columns, doc.RootElement.EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.Clone()).ToArray()).ToList());
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The generator host wired to <see cref="FakeKustoClient"/> and the fixture pack, with the feeder off.</summary>
public sealed class DemoAppFactory : WebApplicationFactory<Program>
{
    public FakeKustoClient Kusto { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Demo:PacksPath", Fixtures.PacksRoot);
        builder.UseSetting("Demo:FeederEnabled", "false");
        builder.UseSetting("Demo:FeederIntervalSeconds", "3600");
        builder.UseSetting("Demo:Scale", "tiny");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IKustoClient>();
            services.AddSingleton<IKustoClient>(Kusto);
        });
    }
}
