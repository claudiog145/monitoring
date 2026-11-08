using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Tests.Spec1000.A3;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.MultiCompany;

/// <summary>
/// Contract v6 (multi-company UI) as code: the optional company theme, ProfileKey on the incident and
/// dashboard stores, the generic profile-journey tables, the §4 Core shapes, the DI stubs each MC task
/// fills in, and the Web proxy routes that must carry the query string (profileKey) to Functions.
/// </summary>
public class ContractV6Tests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string ProfileWithoutTheme = """
        {
          "profileKey": "contract-v6",
          "displayName": "Contract v6 (test)",
          "labels": { "service": "Area", "flow": "Journey", "step": "Step", "entity": "Store", "owner": "Team" },
          "services": [
            {
              "key": "checkout", "displayName": "Checkout", "telemetrySourceAliases": ["v6-web"], "hostFilters": [],
              "flows": [ { "key": "purchase", "displayName": "Purchase", "steps": [
                { "key": "cart", "displayName": "Cart", "order": 1, "matchers": [ { "kind": "path-contains", "value": "/cart" } ] },
                { "key": "pay", "displayName": "Pay", "order": 2, "matchers": [ { "kind": "path-contains", "value": "/pay" } ] }
              ] } ]
            }
          ],
          "entityIdentitySources": [ { "order": 1, "kind": "custom-dimension", "key": "StoreId", "entityKind": "store" } ],
          "glossary": { "serviceNoun": "area", "flowNoun": "journey", "stepNoun": "step", "entityNoun": "store", "ownerNoun": "team",
            "description": "Synthetic profile used only by the contract v6 tests." },
          "enabled": true
        }
        """;

    // ------------------------------------------------------------------ §2 theme

    [Fact]
    public void Theme_IsOptionalInProfileJson()
    {
        var withoutTheme = Profile();
        Assert.Null(withoutTheme.Theme);
        var findings = CompanyProfileValidator.Validate(withoutTheme);
        Assert.False(CompanyProfileValidator.HasErrors(findings));
        Assert.DoesNotContain(findings, f => f.Code.StartsWith("theme-", StringComparison.Ordinal));

        var json = ProfileWithoutTheme.TrimEnd().TrimEnd('}') +
                   """, "theme": { "primaryColor": "#0A2540", "accentColor": "#00b3a4", "shortName": "Contract" } }""";
        var withTheme = JsonSerializer.Deserialize<CompanyProfile>(json, JsonOptions)!;
        Assert.Equal(new CompanyTheme("#0A2540", "#00b3a4", "Contract"), withTheme.Theme);
        Assert.False(CompanyProfileValidator.HasErrors(CompanyProfileValidator.Validate(withTheme)));

        // Every theme member is optional.
        Assert.DoesNotContain(
            CompanyProfileValidator.Validate(withoutTheme with { Theme = new CompanyTheme() }),
            f => f.Code.StartsWith("theme-", StringComparison.Ordinal));

        // Round trip keeps the camelCase shape the UI reads.
        Assert.Contains(
            "\"theme\":{\"primaryColor\":\"#0A2540\",\"accentColor\":\"#00b3a4\",\"shortName\":\"Contract\"}",
            JsonSerializer.Serialize(withTheme, JsonOptions));

        // Theme is the last, optional constructor parameter, so every existing positional call still compiles.
        var parameters = typeof(CompanyProfile).GetConstructors().Single().GetParameters();
        Assert.Equal("Theme", parameters[^1].Name);
        Assert.True(parameters[^1].HasDefaultValue);
        Assert.Null(parameters[^1].DefaultValue);
    }

    [Theory]
    [InlineData("#0A2540", true)]
    [InlineData("#00b3a4", true)]
    [InlineData("#FFFFFF", true)]
    [InlineData("0A2540", false)]
    [InlineData("#0A254", false)]
    [InlineData("#0A25400", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("red", false)]
    [InlineData("", false)]
    [InlineData("#0A2540 ", false)]
    public void Theme_ColorsMustBeHex(string color, bool valid)
    {
        var profile = Profile();

        var primary = CompanyProfileValidator.Validate(profile with { Theme = new CompanyTheme(PrimaryColor: color) });
        var accent = CompanyProfileValidator.Validate(profile with { Theme = new CompanyTheme(AccentColor: color) });

        if (valid)
        {
            Assert.DoesNotContain(primary, f => f.Code == "theme-color-invalid");
            Assert.DoesNotContain(accent, f => f.Code == "theme-color-invalid");
        }
        else
        {
            Assert.Contains(primary, f => f.Code == "theme-color-invalid" && f.Path == "$.theme.primaryColor" && f.IsError);
            Assert.Contains(accent, f => f.Code == "theme-color-invalid" && f.Path == "$.theme.accentColor" && f.IsError);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Banco", null)]
    [InlineData("12345678901234567890123456789012", null)]
    [InlineData("123456789012345678901234567890123", "theme-short-name-too-long")]
    [InlineData("<b>Banco</b>", "theme-short-name-invalid")]
    [InlineData("Banco >", "theme-short-name-invalid")]
    public void Theme_ShortNameLimited(string? shortName, string? expectedCode)
    {
        Assert.Equal(32, CompanyProfileValidator.MaxThemeShortNameLength);

        var themeFindings = CompanyProfileValidator.Validate(Profile() with { Theme = new CompanyTheme(ShortName: shortName) })
            .Where(f => f.Code.StartsWith("theme-", StringComparison.Ordinal))
            .ToList();

        if (expectedCode is null)
        {
            Assert.Empty(themeFindings);
        }
        else
        {
            Assert.Contains(themeFindings, f => f.Code == expectedCode && f.Path == "$.theme.shortName" && f.IsError);
        }
    }

    // ------------------------------------------------------------------ §3 SQL

    [Fact]
    public async Task Sql_ProfileKeyAddedIdempotently()
    {
        var portal = FindPortalDirectory();
        var sqlRoot = Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql");
        var incidentSql = File.ReadAllText(Path.Combine(sqlRoot, "incident", "CreateIncidentSchema.sql"));
        var dashboardSql = File.ReadAllText(Path.Combine(sqlRoot, "dashboard", "CreateDashboardMetrics.sql"));
        var journeySql = File.ReadAllText(Path.Combine(sqlRoot, "monitoring", "CreateMonitoringProfileJourney.sql"));
        var initSql = File.ReadAllText(Path.Combine(portal, "docker", "init-sql.sh"));

        // Registered, after the scripts whose tables it sits beside.
        var journeyAt = initSql.IndexOf("monitoring/CreateMonitoringProfileJourney.sql", StringComparison.Ordinal);
        Assert.True(journeyAt > initSql.IndexOf("incident/CreateIncidentSchema.sql", StringComparison.Ordinal)
                    && journeyAt > initSql.IndexOf("monitoring/CreateMonitoringCompanyProfile.sql", StringComparison.Ordinal),
            "CreateMonitoringProfileJourney.sql must be registered in init-sql.sh after the incident and company profile scripts.");

        foreach (var script in new[] { incidentSql, dashboardSql, journeySql })
        {
            Assert.Equal(Regex.Matches(script, @"(?im)^\s*CREATE\s+TABLE").Count, Regex.Matches(script, @"(?i)IF\s+OBJECT_ID\s*\([^)]*N'U'\s*\)\s*IS\s+NULL").Count);
            Assert.DoesNotMatch(@"(?im)^\s*CREATE\s+PROCEDURE", script);
            Assert.DoesNotMatch(@"(?im)^\s*DROP\s+TABLE", script);
        }

        // Existing rows become the incumbent's; the columns are added only when missing.
        foreach (var table in new[] { "incident.QueryRun", "incident.Incident", "incident.JourneyLink" })
        {
            Assert.Contains($"IF COL_LENGTH(N'{table}', N'ProfileKey') IS NULL", incidentSql);
        }
        Assert.Contains("IF COL_LENGTH(N'dashboard.MetricPoint', N'ProfileKey') IS NULL", dashboardSql);
        Assert.Matches(@"ProfileKey nvarchar\(128\) NOT NULL\s+CONSTRAINT DF_incident_Incident_ProfileKey DEFAULT N'arrivia'", incidentSql);
        Assert.Matches(@"ProfileKey nvarchar\(128\) NOT NULL\s+CONSTRAINT DF_dashboard_MetricPoint_ProfileKey DEFAULT N'arrivia'", dashboardSql);

        // Dedupe key and natural key include ProfileKey; the procedures default to the incumbent.
        Assert.Contains("UNIQUE (ProfileKey, ClubId, Environment, Fingerprint)", incidentSql);
        Assert.Contains("UNIQUE (ProfileKey, Domain, Chart, ClubId, Environment, Series, BinStart)", dashboardSql);
        Assert.Matches(@"@ProfileKey\s+nvarchar\(128\) = N'arrivia'", incidentSql);
        Assert.Matches(@"@ProfileKey\s+nvarchar\(128\) = N'arrivia'", dashboardSql);
        Assert.Contains("CREATE INDEX IX_incident_Incident_Profile", incidentSql);
        Assert.Contains("CREATE INDEX IX_dashboard_MetricPoint_Profile", dashboardSql);
        Assert.Contains("PRIMARY KEY (ProfileKey, JourneyId)", journeySql);
        Assert.Contains("PRIMARY KEY (ProfileKey, JourneyId, StepOrder)", journeySql);

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        // A scratch database, never the shared one: the scripts meet a legacy (pre-v6) shape holding incumbent
        // rows, run twice, and the rows must survive as 'arrivia'.
        var name = "MC0Idem_" + Guid.NewGuid().ToString("N")[..16];
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using (var admin = new SqlConnection(master))
        {
            await admin.ExecuteAsync($"CREATE DATABASE [{name}];");
        }
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name, Pooling = false }.ConnectionString);
            await connection.OpenAsync();

            await RunScriptAsync(connection, incidentSql);
            await connection.ExecuteAsync(LegacyIncidentSql);
            await connection.ExecuteAsync(LegacyDashboardSql);

            for (var run = 0; run < 2; run++)
            {
                await RunScriptAsync(connection, incidentSql);
                await RunScriptAsync(connection, dashboardSql);
                await RunScriptAsync(connection, journeySql);
            }

            Assert.Equal("arrivia", await connection.ExecuteScalarAsync<string>("SELECT ProfileKey FROM incident.Incident WHERE ExternalId = N'EXC-1'"));
            Assert.Equal("arrivia", await connection.ExecuteScalarAsync<string>("SELECT ProfileKey FROM dashboard.MetricPoint WHERE Series = N'legacy'"));
            Assert.Equal("ProfileKey,ClubId,Environment,Fingerprint", await UniqueColumnsAsync(connection, "incident.Incident", "UQ_incident_Incident_Fingerprint"));
            Assert.Equal("ProfileKey,Domain,Chart,ClubId,Environment,Series,BinStart", await UniqueColumnsAsync(connection, "dashboard.MetricPoint", "UQ_dashboard_MetricPoint"));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'monitoring') AND name IN (N'ProfileJourney', N'ProfileJourneyStep')"));

            // Same fingerprint in two companies is two incidents; the incumbent call (no @ProfileKey) is unchanged.
            await IngestAsync(connection, "hotels", null);
            await IngestAsync(connection, "hotels", null);
            await IngestAsync(connection, "payments", "banco");
            Assert.Equal(
                ["arrivia|hotels", "banco|payments"],
                (await connection.QueryAsync<string>("SELECT ProfileKey + N'|' + Domain FROM incident.Incident WHERE Fingerprint = N'mc0-fp' ORDER BY ProfileKey")).ToList());

            // The incumbent keeps its domain list; another company uses its own service keys.
            await Assert.ThrowsAsync<SqlException>(() => IngestAsync(connection, "payments", null));

            await ReplaceChartAsync(connection, "hotels", null);
            await ReplaceChartAsync(connection, "payments", "banco");
            Assert.Equal(
                ["arrivia|hotels", "banco|payments"],
                (await connection.QueryAsync<string>("SELECT ProfileKey + N'|' + Domain FROM dashboard.MetricPoint WHERE Chart = N'mc0' ORDER BY ProfileKey")).ToList());
            var legacyRead = await connection.QueryAsync<string>(
                "dashboard.usp_GetChartPoints", new { Chart = "mc0" }, commandType: CommandType.StoredProcedure);
            Assert.Equal(["hotels"], legacyRead.ToList());
            var bancoRead = await connection.QueryAsync<string>(
                "dashboard.usp_GetChartPoints", new { Chart = "mc0", ProfileKey = "banco" }, commandType: CommandType.StoredProcedure);
            Assert.Equal(["payments"], bancoRead.ToList());
        }
        finally
        {
            await using var admin = new SqlConnection(master);
            await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    // ------------------------------------------------------------------ Web proxy (§4, §5)

    [Fact]
    public void Proxy_ProfileJourneysForwardsQueryString()
    {
        var program = WebProgram();

        AssertForwardsQueryString(program, "/api/profile-journeys", @"/api/profile-journeys\{\w+\.Request\.QueryString\}");
        AssertForwardsQueryString(program, "/api/profile-journeys/{journeyId}", @"/api/profile-journeys/\{Uri\.EscapeDataString\(journeyId\)\}\{\w+\.Request\.QueryString\}");
        AssertForwardsQueryString(program, "/api/entities", @"/api/entities\{\w+\.Request\.QueryString\}");
        AssertForwarderKeepsPath(program);
    }

    [Fact]
    public void Proxy_ClubInventoryForwardsQueryString()
    {
        var program = WebProgram();

        AssertForwardsQueryString(program, "/api/club-inventory", @"/api/club-inventory\{\w+\.Request\.QueryString\}");
        Assert.DoesNotContain("GetClubInventoryAsync(cancellationToken)", program);
        AssertForwarderKeepsPath(program);
    }

    // ------------------------------------------------------------------ §4 shapes and DI stubs

    [Fact]
    public void ProfileReads_ResponseShapesMatchContract()
    {
        var journey = new ProfileJourney("banco", "op-1", "payments", "transfer", "branch-7",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), 2000, ProfileJourneyStatus.DroppedAt(2), 2, "confirm", 2, false, "op-1");
        var step = new ProfileJourneyStep("banco", "op-1", 1, "start", "POST /transfer", DateTimeOffset.UnixEpoch, 120, true, "200");
        var labels = new ProfileJourneyLabels("Product", "Flow", "Stage", "Branch");

        var list = JsonSerializer.SerializeToElement(new ProfileJourneyListResponse("banco", labels, [journey]), JsonOptions);
        Assert.Equal(["profileKey", "labels", "journeys"], list.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["service", "flow", "step", "entity"], list.GetProperty("labels").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["profileKey", "journeyId", "serviceKey", "flowKey", "entityKey", "startedAt", "endedAt", "durationMs", "status",
             "lastStepOrder", "lastStepKey", "stepCount", "failed", "sampleOperationId"],
            list.GetProperty("journeys")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Dropped at step 2", list.GetProperty("journeys")[0].GetProperty("status").GetString());

        var detail = JsonSerializer.SerializeToElement(new ProfileJourneyDetailResponse("banco", labels, journey, [step]), JsonOptions);
        Assert.Equal(["profileKey", "labels", "journey", "steps"], detail.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["profileKey", "journeyId", "stepOrder", "stepKey", "name", "timestamp", "durationMs", "success", "resultCode"],
            detail.GetProperty("steps")[0].EnumerateObject().Select(p => p.Name));

        var entities = JsonSerializer.SerializeToElement(new EntityListResponse("Branch", [new EntityCount("branch-7", 10, 1)]), JsonOptions);
        Assert.Equal(["entityLabel", "entities"], entities.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["key", "requests", "failures"], entities.GetProperty("entities")[0].EnumerateObject().Select(p => p.Name));

        Assert.True(typeof(IProfileJourneyStore).IsInterface);
        Assert.True(typeof(IProfileJourneyReader).IsInterface);
        Assert.True(typeof(IEntityCatalog).IsInterface);
    }

    [Fact]
    public void ProfileDiStubs_AreCalledFromFunctionsAndRegisterNothingYet()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        Assert.Same(services, services.AddProfileJourneys(configuration));
        Assert.Same(services, services.AddProfileExceptions(configuration));
        Assert.Same(services, services.AddProfileAnalytics(configuration));
        Assert.Same(services, services.AddProfileOverview(configuration));
        Assert.Empty(services);

        var portal = FindPortalDirectory();
        var functionsProgram = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Functions", "Program.cs"));
        var build = functionsProgram.IndexOf("builder.Build().Run();", StringComparison.Ordinal);
        foreach (var stub in new[] { "AddProfileJourneys", "AddProfileExceptions", "AddProfileAnalytics", "AddProfileOverview" })
        {
            var call = functionsProgram.IndexOf($"builder.Services.{stub}(builder.Configuration);", StringComparison.Ordinal);
            Assert.True(call >= 0 && call < build, $"Functions Program.cs must call {stub} before Build().");
            Assert.True(File.Exists(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "DependencyInjection", $"{stub}.cs")),
                $"{stub} must live in its own file so each MC task owns it.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static CompanyProfile Profile() => JsonSerializer.Deserialize<CompanyProfile>(ProfileWithoutTheme, JsonOptions)!;

    private static string WebProgram() =>
        File.ReadAllText(Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Web", "Program.cs"));

    /// <summary>The route is an expression-bodied proxy whose forwarded path ends with the incoming query string.</summary>
    private static void AssertForwardsQueryString(string program, string route, string forwardedPathPattern)
    {
        var start = program.IndexOf($"app.MapGet(\"{route}\",", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Web Program.cs must map GET {route}.");
        var statement = program[start..program.IndexOf(';', start)];
        Assert.Matches(@"ForwardCatalogGetAsync\(\s*\w+,\s*\w+,\s*\$""" + forwardedPathPattern + @""",", statement);
        Assert.Contains(".AllowAnonymous()", statement);
    }

    // The shared forwarder must hand the path (query string included) to the Functions client unchanged.
    private static void AssertForwarderKeepsPath(string program) =>
        Assert.Matches(
            @"static async Task<IResult> ForwardCatalogGetAsync\(HttpContext context, IJourneyMonitorApiClient client, string path, CancellationToken ct\)\s*\{\s*var \(statusCode, body\) = await client\.GetSettingsCatalogAsync\(path, ct\);",
            program);

    private static async Task IngestAsync(SqlConnection connection, string domain, string? profileKey)
    {
        var incidents = JsonSerializer.Serialize(new[]
        {
            new
            {
                Fingerprint = "mc0-fp", ClubId = "1", ExceptionType = "E", Component = "C", Operation = "O", Message = "M",
                FirstSeen = DateTimeOffset.UnixEpoch, LastSeen = DateTimeOffset.UnixEpoch,
                Occurrences = Array.Empty<object>(), JourneyKeys = Array.Empty<string>(),
            },
        });
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain);
        parameters.Add("Environment", "prod");
        parameters.Add("Window", "1h");
        parameters.Add("FallbackClubId", "1");
        parameters.Add("IncidentsJson", incidents);
        if (profileKey is not null)
        {
            parameters.Add("ProfileKey", profileKey);
        }
        await connection.ExecuteAsync("incident.usp_IngestExceptions", parameters, commandType: CommandType.StoredProcedure);
    }

    private static async Task ReplaceChartAsync(SqlConnection connection, string domain, string? profileKey)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain);
        parameters.Add("Chart", "mc0");
        parameters.Add("Window", "1h");
        parameters.Add("RowsJson", "[{\"binStart\":\"2026-11-07T00:00:00Z\",\"series\":\"s\",\"value\":1}]");
        if (profileKey is not null)
        {
            parameters.Add("ProfileKey", profileKey);
        }
        await connection.ExecuteAsync("dashboard.usp_ReplaceChartPoints", parameters, commandType: CommandType.StoredProcedure);
    }

    private static async Task<string> UniqueColumnsAsync(SqlConnection connection, string table, string constraint) =>
        string.Join(",", await connection.QueryAsync<string>(
            """
            SELECT c.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@Table) AND i.name = @Constraint
            ORDER BY ic.key_ordinal
            """,
            new { Table = table, Constraint = constraint }));

    // Turns the freshly created incident.Incident back into its pre-v6 shape and stores an incumbent row in it.
    private const string LegacyIncidentSql = """
        ALTER TABLE incident.Incident DROP CONSTRAINT UQ_incident_Incident_Fingerprint;
        DROP INDEX IX_incident_Incident_Profile ON incident.Incident;
        ALTER TABLE incident.Incident DROP CONSTRAINT CK_incident_Incident_Domain;
        ALTER TABLE incident.Incident DROP CONSTRAINT DF_incident_Incident_ProfileKey;
        ALTER TABLE incident.Incident DROP COLUMN ProfileKey;
        ALTER TABLE incident.Incident ADD CONSTRAINT UQ_incident_Incident_Fingerprint UNIQUE (ClubId, Environment, Fingerprint);
        ALTER TABLE incident.Incident ADD CONSTRAINT CK_incident_Incident_Domain CHECK (Domain IN (N'hotels', N'cruises', N'cars', N'air'));
        DECLARE @Run uniqueidentifier = NEWID();
        INSERT INTO incident.QueryRun (QueryRunId, Domain, ClubId, Environment, Window, [RowCount], Status)
        VALUES (@Run, N'hotels', N'1', N'prod', N'1h', 0, N'Succeeded');
        INSERT INTO incident.Incident (ExternalId, Fingerprint, Domain, ClubId, Environment, ExceptionType, Component, Operation,
            Message, Severity, Status, FirstSeen, LastSeen, LastQueryRunId)
        VALUES (N'EXC-1', N'legacy', N'hotels', N'1', N'prod', N'T', N'C', N'O', N'M', N'Low', N'Open',
            SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @Run);
        """;

    // The pre-v6 dashboard.MetricPoint, with one incumbent row.
    private const string LegacyDashboardSql = """
        EXEC(N'CREATE SCHEMA dashboard');
        CREATE TABLE dashboard.MetricPoint
        (
            MetricPointId bigint NOT NULL IDENTITY(1, 1) CONSTRAINT PK_dashboard_MetricPoint PRIMARY KEY,
            Domain nvarchar(32) NOT NULL,
            Chart nvarchar(64) NOT NULL,
            Series nvarchar(128) NOT NULL,
            BinStart datetimeoffset(7) NOT NULL,
            Value decimal(18, 2) NOT NULL,
            Window nvarchar(16) NOT NULL,
            Environment nvarchar(32) NULL,
            FetchedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_dashboard_MetricPoint_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
            ClubId nvarchar(32) NOT NULL CONSTRAINT DF_dashboard_MetricPoint_ClubId DEFAULT N'',
            CONSTRAINT UQ_dashboard_MetricPoint UNIQUE (Domain, Chart, ClubId, Environment, Series, BinStart),
            CONSTRAINT CK_dashboard_MetricPoint_Domain CHECK (Domain IN (N'hotels', N'cruises', N'cars', N'air'))
        );
        INSERT INTO dashboard.MetricPoint (Domain, Chart, Series, BinStart, Value, Window)
        VALUES (N'hotels', N'step_latency_p95', N'legacy', SYSDATETIMEOFFSET(), 1, N'1h');
        """;

    private static async Task RunScriptAsync(SqlConnection connection, string script)
    {
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                await connection.ExecuteAsync(batch, commandTimeout: 120);
            }
        }
    }

    private static string FindPortalDirectory([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docker", "init-sql.sh"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Portal", "docker", "init-sql.sh"))) return Path.Combine(dir.FullName, "Portal");
            }
        }
        throw new InvalidOperationException("Could not find the Portal directory (docker/init-sql.sh).");
    }
}
