using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.HealthConsole;

/// <summary>
/// Calibration console for the Specification 1000 KPI rules. Runs the real <see cref="KpiEvaluator"/>
/// against in-memory or stored buckets and prints what it would decide. It never writes: stores are
/// wrapped in read-only decorators and nothing is ever saved.
/// </summary>
public static partial class HealthConsoleApp
{
    public const string Usage =
        """
        JourneyMonitor.HealthConsole - calibrate KPI rules without the UI and without writing anything.

        Usage:
          simulate --scenario afternoon
              Runs the presentation's afternoon simulation in memory and prints the table.
          evaluate --kpi <id> [--hours 48] [--connection <sql>]
              Reads the real definition, state and buckets and shows what the evaluator would do now (nothing is saved).
          replay --kpi <id> --from <iso> --to <iso> [--method <m>] [--param k=v ...] [--consecutive n] [--cooldown m] [--connection <sql>]
              Re-evaluates historical buckets with the stored definition or with overrides, starting from an empty state.
          kpis [--connection <sql>] [--profile <key>]
              Lists the KPI definitions with their company profile. Without --profile, every profile.
          status [--connection <sql>] [--profile <key>]
              One line per daemon: name, health, last run (UTC) and its summary, plus the last error if any.
              Then enabled/total KPIs per Profile and pillar (every profile unless --profile is given).
          watch [--daemon <key>] [--level info|warning|error] [--interval <s>] [--since <n>] [--max-polls <n>] [--profile <key>] [--connection <sql>]
              Prints the last --since (default 50, 0..1000) activity entries, then polls every --interval seconds
              (default 3, 1..60) and prints only new entries. --level is the minimum level. Ctrl+C stops it;
              --max-polls stops after n polls. Daemon keys: kpi-ingestion, application-health-monitor,
              business-health-monitor, infrastructure-health-monitor, triage-monkey. The Profile column shows the
              company profile of KPI lines (- for platform-wide lines); --profile hides the lines of other profiles.
          --help
              Shows this help.

        Exit codes: 0 ok, 1 invalid arguments, 2 KPI not found, 3 store/connection error.
        Configuration: appsettings.json (optional), environment variables (JourneyStore__ConnectionString or
        platform_reliability_database_connectionString) and --connection, which wins over both. status reads
        HealthMonitoring:* and Triage:* from the same sources to decide which daemons are enabled.

        """;

    private const string TableHeader = "Hora | Observado | Referencia | Desvío | Veredicto | Outcome | Contador";
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitNotFound = 2;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, string[]> CommandOptions = new(StringComparer.Ordinal)
    {
        ["simulate"] = ["scenario"],
        ["evaluate"] = ["kpi", "hours", "connection"],
        ["replay"] = ["kpi", "from", "to", "method", "param", "consecutive", "cooldown", "connection"],
        ["status"] = ["profile", "connection"],
        ["watch"] = ["daemon", "level", "interval", "since", "max-polls", "profile", "connection"],
        ["kpis"] = ["profile", "connection"],
    };

    public static Task<int> RunAsync(string[] args, TextWriter output, IServiceProvider? services = null)
        => RunAsync(args, output, services, delay: null, CancellationToken.None);

    /// <param name="delay">Sleep between watch polls; defaults to Task.Delay on the service provider's TimeProvider.</param>
    /// <param name="ct">Cancelled by Ctrl+C; watch then exits cleanly with 0.</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, IServiceProvider? services, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        args ??= [];

        if (args.Any(a => a is "--help" or "-h"))
        {
            await output.WriteAsync(Usage);
            return ExitOk;
        }

        if (args.Length == 0)
        {
            await output.WriteAsync(Usage);
            return ExitUsage;
        }

        if (!TryParse(args, out var command, out var options, out var error))
        {
            return await InvalidAsync(output, error);
        }

        switch (command)
        {
            case "simulate":
                return await SimulateAsync(options, output);
            case "evaluate":
            case "replay":
            case "kpis":
                ServiceProvider? owned = null;
                try
                {
                    if (services is null)
                    {
                        owned = BuildServices(options.Single("connection"));
                        services = owned;
                    }
                    return command switch
                    {
                        "evaluate" => await EvaluateAsync(options, output, services),
                        "kpis" => await KpisAsync(options, output, services),
                        _ => await ReplayAsync(options, output, services),
                    };
                }
                finally
                {
                    if (owned is not null) await owned.DisposeAsync();
                }
            case "status":
            case "watch":
                WatchSettings? watch = null;
                if (command == "watch" && !TryGetWatchSettings(options, out watch, out var watchError))
                {
                    return await InvalidAsync(output, watchError);
                }
                ServiceProvider? ownedDaemons = null;
                try
                {
                    if (services is null)
                    {
                        ownedDaemons = BuildDaemonServices(options.Single("connection"));
                        services = ownedDaemons;
                    }
                    return watch is null
                        ? await StatusAsync(output, services, ProfileFilter(options), ct)
                        : await WatchAsync(watch, output, services, delay, ct);
                }
                finally
                {
                    if (ownedDaemons is not null) await ownedDaemons.DisposeAsync();
                }
            default:
                return await InvalidAsync(output, $"Unknown command '{command}'.");
        }
    }

    private static async Task<int> InvalidAsync(TextWriter output, string error)
    {
        await output.WriteLineAsync("Error: " + error);
        await output.WriteLineAsync();
        await output.WriteAsync(Usage);
        return ExitUsage;
    }

    // ---------------------------------------------------------------- simulate

    private static readonly DateTimeOffset SimulationDay = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly (int Hour, double Normal, double Observed)[] AfternoonTable =
    [
        (12, 1800, 1854),
        (13, 1820, 2950),
        (14, 1880, 1974),
        (15, 1900, 3230),
        (16, 1910, 3534),
        (17, 1930, 3667),
        (18, 1900, 2660),
    ];

    private static async Task<int> SimulateAsync(ParsedOptions options, TextWriter output)
    {
        var scenario = options.Single("scenario");
        if (scenario is null) return await InvalidAsync(output, "simulate requires --scenario afternoon.");
        if (!string.Equals(scenario, "afternoon", StringComparison.OrdinalIgnoreCase))
            return await InvalidAsync(output, $"Unknown scenario '{scenario}'. Available: afternoon.");

        // Same data as KpiEvaluatorTests.AfternoonSimulation_MatchesPresentationTable (Spec 1000 A1).
        var definition = new KpiDefinition(
            KpiDefinitionId: 1,
            Pillar: KpiPillar.Application,
            Kpi: "Latency",
            Domain: "hotels",
            Stage: "",
            SourceKey: "appinsights:latency",
            Direction: KpiDirection.Increase,
            Aggregation: KpiAggregation.P95,
            EvaluationFrequency: TimeSpan.FromMinutes(5),
            BucketSize: TimeSpan.FromHours(1),
            LookbackWindow: TimeSpan.FromDays(1),
            SampleSize: 0,
            MinObservationsPerBucket: 30,
            DetectionMethod: DetectionMethodKind.BaselinePercent,
            MethodParamsJson: "{\"lookbackDays\":7,\"thresholdPercent\":50}",
            ConsecutiveCount: 2,
            CooldownMinutes: 120,
            Enabled: true,
            UpdatedAt: SimulationDay,
            UpdatedBy: "health-console");

        var history = AfternoonTable
            .SelectMany(row => Enumerable.Range(1, 7).Select(k => P95Bucket(SimulationDay.AddHours(row.Hour).AddDays(-k), row.Normal)))
            .OrderBy(b => b.BucketStart)
            .ToList();
        var buckets = AfternoonTable.Select(row => P95Bucket(SimulationDay.AddHours(row.Hour), row.Observed)).ToList();

        var results = new KpiEvaluator().Evaluate(definition, KpiDetectionState.Initial(definition.KpiDefinitionId), history, buckets);

        await output.WriteLineAsync("Scenario: afternoon (in memory, nothing is read or written)");
        await WriteDefinitionAsync(output, definition);
        await output.WriteLineAsync(string.Create(Inv, $"Date (UTC): {SimulationDay:yyyy-MM-dd}; history: 7 previous days per hour at the 'normal' P95; TotalCount 5000 per bucket."));
        await output.WriteLineAsync();
        await WriteTableAsync(output, definition, results);
        await WriteSummaryAsync(output, results);
        return ExitOk;
    }

    private static KpiBucket P95Bucket(DateTimeOffset start, double p95) =>
        new(1, start, 5000, 0, null, p95, null, null, null);

    // ---------------------------------------------------------------- evaluate

    private static async Task<int> EvaluateAsync(ParsedOptions options, TextWriter output, IServiceProvider services)
    {
        if (!TryGetKpiId(options, out var kpiId, out var error)) return await InvalidAsync(output, error);
        var hours = 48;
        if (options.Single("hours") is { } hoursText
            && (!int.TryParse(hoursText, NumberStyles.Integer, Inv, out hours) || hours < 1))
        {
            return await InvalidAsync(output, $"--hours must be a positive integer, got '{hoursText}'.");
        }

        var stores = ReadOnlyServices.From(services);
        var definition = await stores.Definitions.GetAsync(kpiId);
        if (definition is null)
        {
            await output.WriteLineAsync(string.Create(Inv, $"KPI {kpiId} not found."));
            return ExitNotFound;
        }

        var now = stores.Time.GetUtcNow().ToUniversalTime();
        var state = await stores.States.GetAsync(kpiId);
        DateTimeOffset? watermark = stores.Watermarks is null ? now : await stores.Watermarks.GetAsync(kpiId);

        await output.WriteLineAsync(string.Create(Inv, $"Evaluate KPI {kpiId} (read-only preview, nothing is saved)"));
        await WriteDefinitionAsync(output, definition);
        await output.WriteLineAsync(string.Create(Inv, $"Now (UTC): {now:yyyy-MM-dd HH:mm}; watermark: {FormatInstant(watermark)}"));
        await output.WriteLineAsync(string.Create(Inv,
            $"State: counter {state.ConsecutiveCount}; last bucket {FormatInstant(state.LastBucketEvaluated)}; cooldown until {FormatInstant(state.CooldownUntil)}; last evaluated {FormatInstant(state.LastEvaluatedAt)}"));
        var due = state.LastEvaluatedAt is not { } last || now - last >= definition.EvaluationFrequency;
        await output.WriteLineAsync(due
            ? "Due: yes"
            : string.Create(Inv, $"Due: no (next run after {state.LastEvaluatedAt!.Value + definition.EvaluationFrequency:yyyy-MM-dd HH:mm}); showing what it would do once due."));
        if (!definition.Enabled) await output.WriteLineAsync("Note: the KPI is disabled; the monitor would not evaluate it.");

        if (watermark is not { } ingestedUntil)
        {
            await output.WriteLineAsync("No ingestion watermark: the monitor would not evaluate any bucket.");
            return ExitOk;
        }

        // Same bucket selection as the pillar monitor: closed and fully ingested; only the latest one
        // on a first run, otherwise everything after LastBucketEvaluated (bounded by --hours).
        var limit = ingestedUntil < now ? ingestedUntil : now;
        var end = KpiTime.FloorToBucket(limit, definition.BucketSize);
        var windowStart = end - TimeSpan.FromHours(hours);
        var candidates = (await stores.Buckets.GetRangeAsync(kpiId, windowStart, end))
            .Where(b => b.BucketStart + definition.BucketSize <= limit)
            .OrderBy(b => b.BucketStart)
            .ToList();
        var pending = state.LastBucketEvaluated is { } lastBucket
            ? candidates.Where(b => b.BucketStart > lastBucket).ToList()
            : candidates.TakeLast(1).ToList();

        await output.WriteLineAsync(string.Create(Inv, $"Pending buckets in the last {hours} h: {pending.Count}"));
        await output.WriteLineAsync();
        if (pending.Count == 0)
        {
            await output.WriteLineAsync("Nothing to evaluate.");
            return ExitOk;
        }

        var evaluator = new KpiEvaluator();
        var first = pending[0].BucketStart;
        var history = await stores.Buckets.GetRangeAsync(kpiId, first - evaluator.GetRequiredHistory(definition), first);
        var results = evaluator.Evaluate(definition, state, history, pending);

        await WriteTableAsync(output, definition, results);
        await WriteSummaryAsync(output, results);
        var final = results[^1].StateAfter;
        await output.WriteLineAsync(string.Create(Inv,
            $"Resulting state (not saved): counter {final.ConsecutiveCount}; last bucket {FormatInstant(final.LastBucketEvaluated)}; cooldown until {FormatInstant(final.CooldownUntil)}"));
        return ExitOk;
    }

    // ---------------------------------------------------------------- replay

    private static async Task<int> ReplayAsync(ParsedOptions options, TextWriter output, IServiceProvider services)
    {
        if (!TryGetKpiId(options, out var kpiId, out var error)) return await InvalidAsync(output, error);
        if (!TryGetInstant(options, "from", out var from, out error)) return await InvalidAsync(output, error);
        if (!TryGetInstant(options, "to", out var to, out error)) return await InvalidAsync(output, error);
        if (to <= from) return await InvalidAsync(output, "--to must be after --from.");

        DetectionMethodKind? method = null;
        if (options.Single("method") is { } methodText)
        {
            if (!Enum.TryParse<DetectionMethodKind>(methodText, ignoreCase: true, out var parsedMethod) || !Enum.IsDefined(parsedMethod))
                return await InvalidAsync(output, $"--method must be one of {string.Join(", ", Enum.GetNames<DetectionMethodKind>())}, got '{methodText}'.");
            method = parsedMethod;
        }

        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var raw in options.All("param"))
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0) return await InvalidAsync(output, $"--param must be k=v, got '{raw}'.");
            parameters.Add(new(raw[..separator].Trim(), raw[(separator + 1)..].Trim()));
        }

        if (!TryGetOptionalInt(options, "consecutive", out var consecutive, out error)) return await InvalidAsync(output, error);
        if (!TryGetOptionalInt(options, "cooldown", out var cooldown, out error)) return await InvalidAsync(output, error);

        var stores = ReadOnlyServices.From(services);
        var stored = await stores.Definitions.GetAsync(kpiId);
        if (stored is null)
        {
            await output.WriteLineAsync(string.Create(Inv, $"KPI {kpiId} not found."));
            return ExitNotFound;
        }

        var definition = stored;
        if (method is { } m && m != stored.DetectionMethod)
            definition = definition with { DetectionMethod = m, MethodParamsJson = "{}" };
        if (parameters.Count > 0)
            definition = definition with { MethodParamsJson = MergeParams(definition.MethodParamsJson, parameters) };
        if (consecutive is { } c) definition = definition with { ConsecutiveCount = c };
        if (cooldown is { } cd) definition = definition with { CooldownMinutes = cd };

        var validation = definition.Validate();
        if (validation.Count > 0)
        {
            return await InvalidAsync(output, "the overridden rule is invalid: " + string.Join(" ", validation));
        }

        var evaluator = new KpiEvaluator();
        var buckets = await stores.Buckets.GetRangeAsync(kpiId, from, to);
        var history = await stores.Buckets.GetRangeAsync(kpiId, from - evaluator.GetRequiredHistory(definition), from);
        var results = evaluator.Evaluate(definition, KpiDetectionState.Initial(kpiId), history, buckets);

        var overridden = !ReferenceEquals(definition, stored);
        await output.WriteLineAsync(string.Create(Inv,
            $"Replay KPI {kpiId} from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC ({(overridden ? "with overrides" : "stored definition")}, initial state, nothing is saved)"));
        await WriteDefinitionAsync(output, definition);
        await output.WriteLineAsync();
        await WriteTableAsync(output, definition, results);
        await WriteSummaryAsync(output, results);
        return ExitOk;
    }

    private static string MergeParams(string baseJson, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(baseJson) ? "{}" : baseJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }

        foreach (var (key, value) in parameters)
        {
            if (long.TryParse(value, NumberStyles.Integer, Inv, out var integer)) root[key] = integer;
            else if (double.TryParse(value, NumberStyles.Float, Inv, out var number)) root[key] = number;
            else if (bool.TryParse(value, out var flag)) root[key] = flag;
            else root[key] = value;
        }

        return root.ToJsonString();
    }

    // ---------------------------------------------------------------- output

    private static async Task WriteDefinitionAsync(TextWriter output, KpiDefinition d)
    {
        var stage = d.Stage.Length == 0 ? "(all stages)" : d.Stage;
        await output.WriteLineAsync(string.Create(Inv,
            $"Rule: {d.Pillar}/{d.Kpi}/{d.Domain}/{stage} {d.Aggregation} {d.Direction} {d.DetectionMethod} {d.MethodParamsJson}; consecutive {d.ConsecutiveCount}; cooldown {d.CooldownMinutes} min; min observations {d.MinObservationsPerBucket}; bucket {d.BucketSize.TotalMinutes} min"));
    }

    private static async Task WriteTableAsync(TextWriter output, KpiDefinition definition, IReadOnlyList<KpiEvaluation> results)
    {
        await output.WriteLineAsync(TableHeader);
        DateTimeOffset? previousDay = null;
        foreach (var r in results)
        {
            var start = r.Bucket.BucketStart.ToUniversalTime();
            // Hours are HH:mm; a date line separates days when a range spans more than one.
            if (previousDay is { } day && start.Date != day.Date)
                await output.WriteLineAsync(string.Create(Inv, $"-- {start:yyyy-MM-dd} --"));
            previousDay = start;

            await output.WriteLineAsync(string.Join(" | ",
                start.ToString("HH:mm", Inv),
                FormatValue(r.ObservedValue),
                FormatValue(r.ReferenceValue),
                FormatDeviation(definition, r.Deviation),
                r.Verdict.ToString(),
                r.Outcome.ToString(),
                r.StateAfter.ConsecutiveCount.ToString(Inv)));
        }
    }

    private static async Task WriteSummaryAsync(TextWriter output, IReadOnlyList<KpiEvaluation> results)
    {
        await output.WriteLineAsync();
        var triggers = results.Count(r => r.Outcome == KpiEvaluationOutcome.Triggered);
        var suppressed = results.Count(r => r.Outcome == KpiEvaluationOutcome.SuppressedByCooldown);
        var skipped = results.Count(r => r.Outcome == KpiEvaluationOutcome.Skipped);
        await output.WriteLineAsync(string.Create(Inv,
            $"Summary: {results.Count} buckets, {triggers} triggers, {suppressed} suppressed, {skipped} skipped"));
        foreach (var r in results.Where(r => r.Outcome is KpiEvaluationOutcome.Triggered or KpiEvaluationOutcome.Skipped))
        {
            var label = r.Outcome == KpiEvaluationOutcome.Triggered ? "Trigger" : "Skipped";
            await output.WriteLineAsync(string.Create(Inv, $"  {label} {r.Bucket.BucketStart.ToUniversalTime():yyyy-MM-dd HH:mm}: {r.Explanation}"));
        }
    }

    private static string FormatValue(double? value) => value is { } v ? v.ToString("0.##", Inv) : "-";

    private static string FormatDeviation(KpiDefinition definition, double? deviation)
    {
        if (deviation is not { } d) return "-";
        return definition.DetectionMethod switch
        {
            DetectionMethodKind.BaselinePercent => d.ToString("+0.0;-0.0;+0.0", Inv) + "%",
            DetectionMethodKind.RobustZScore => "z=" + d.ToString("+0.0;-0.0;+0.0", Inv),
            _ => d.ToString("+0.##;-0.##;+0", Inv),
        };
    }

    private static string FormatInstant(DateTimeOffset? value) =>
        value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", Inv) : "-";

    // ---------------------------------------------------------------- arguments

    private static bool TryParse(string[] args, out string command, out ParsedOptions options, out string error)
    {
        command = args[0];
        options = new ParsedOptions();
        error = "";

        if (!CommandOptions.TryGetValue(command, out var allowed))
        {
            error = command.StartsWith('-') ? $"Expected a command before '{command}'." : $"Unknown command '{command}'.";
            return false;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                error = $"Unexpected argument '{token}'.";
                return false;
            }

            var name = token[2..];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                error = $"Unknown option '{token}' for '{command}'.";
                return false;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{token}' requires a value.";
                return false;
            }

            var value = args[++i];
            if (name != "param" && options.Single(name) is not null)
            {
                error = $"Option '{token}' was given more than once.";
                return false;
            }
            options.Add(name, value);
        }

        return true;
    }

    private static bool TryGetKpiId(ParsedOptions options, out int kpiId, out string error)
    {
        kpiId = 0;
        error = "";
        var text = options.Single("kpi");
        if (text is null)
        {
            error = "--kpi <id> is required.";
            return false;
        }
        if (!int.TryParse(text, NumberStyles.Integer, Inv, out kpiId) || kpiId < 1)
        {
            error = $"--kpi must be a positive integer, got '{text}'.";
            return false;
        }
        return true;
    }

    private static bool TryGetInstant(ParsedOptions options, string name, out DateTimeOffset value, out string error)
    {
        value = default;
        error = "";
        var text = options.Single(name);
        if (text is null)
        {
            error = $"--{name} <iso> is required.";
            return false;
        }
        if (!DateTimeOffset.TryParse(text, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            error = $"--{name} must be an ISO-8601 date/time, got '{text}'.";
            return false;
        }
        return true;
    }

    private static bool TryGetOptionalInt(ParsedOptions options, string name, out int? value, out string error)
    {
        value = null;
        error = "";
        if (options.Single(name) is not { } text) return true;
        if (!int.TryParse(text, NumberStyles.Integer, Inv, out var parsed))
        {
            error = $"--{name} must be an integer, got '{text}'.";
            return false;
        }
        value = parsed;
        return true;
    }

    // ---------------------------------------------------------------- services

    private static ServiceProvider BuildServices(string? connection) =>
        new ServiceCollection()
            .AddKpiPersistence(BuildConfiguration(connection))
            .BuildServiceProvider();

    private static IConfiguration BuildConfiguration(string? connection)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables();
        if (!string.IsNullOrWhiteSpace(connection))
        {
            // AddKpiPersistence gives the App Configuration key precedence, so --connection sets it.
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [JourneyStoreOptions.AppConfigKey] = connection,
                [$"{JourneyStoreOptions.SectionName}:ConnectionString"] = connection,
            });
        }
        return builder.Build();
    }

    private sealed class ParsedOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        public void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out var list)) _values[name] = list = [];
            list.Add(value);
        }

        public string? Single(string name) => _values.TryGetValue(name, out var list) ? list[0] : null;

        public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : [];
    }

    private sealed record ReadOnlyServices(
        IKpiDefinitionStore Definitions,
        IKpiBucketStore Buckets,
        IKpiStateStore States,
        IIngestionWatermarkStore? Watermarks,
        TimeProvider Time)
    {
        public static ReadOnlyServices From(IServiceProvider services)
        {
            var watermarks = services.GetService<IIngestionWatermarkStore>();
            return new ReadOnlyServices(
                new ReadOnlyKpiDefinitionStore(services.GetRequiredService<IKpiDefinitionStore>()),
                new ReadOnlyKpiBucketStore(services.GetRequiredService<IKpiBucketStore>()),
                new ReadOnlyKpiStateStore(services.GetRequiredService<IKpiStateStore>()),
                watermarks is null ? null : new ReadOnlyIngestionWatermarkStore(watermarks),
                services.GetService<TimeProvider>() ?? TimeProvider.System);
        }
    }
}

