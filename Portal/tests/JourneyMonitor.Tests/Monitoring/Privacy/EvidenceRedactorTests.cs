using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Privacy;

public class EvidenceRedactorTests
{
    private const string Email = "jane.doe@example.com";
    private const string Phone = "+1 (555) 123-4567";
    private const string Card = "4111 1111 1111 1111";
    private const string MemberId = "MBR-998877";

    [Fact]
    public void Redactor_MasksEmailPhoneCard()
    {
        var redactor = new EvidenceRedactor();
        var text = $"user {Email} called from {Phone}, paid with {Card}; ssn 123-45-6789; " +
                   "token Bearer abc.def-ghi; jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJl; ip 10.20.30.40";

        var result = redactor.Redact(text, Profile("acme", new PrivacySettings()));

        Assert.DoesNotContain(Email, result.Text);
        Assert.DoesNotContain("555", result.Text);
        Assert.DoesNotContain("4111 1111", result.Text);
        Assert.DoesNotContain("41111111", result.Text);
        Assert.DoesNotContain("123-45-6789", result.Text);
        Assert.DoesNotContain("abc.def-ghi", result.Text);
        Assert.DoesNotContain("eyJhbGci", result.Text);
        Assert.DoesNotContain("10.20.30.40", result.Text);
        Assert.Contains("[REDACTED:email]", result.Text);
        Assert.Contains("[REDACTED:phone]", result.Text);
        Assert.Contains("[REDACTED:card]", result.Text);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Email]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Phone]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Card]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Ssn]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Bearer]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Jwt]);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Ipv4]);

        // Deterministic: same input, same output.
        Assert.Equal(result.Text, redactor.Redact(text, Profile("acme", new PrivacySettings())).Text);
    }

    [Fact]
    public void Redactor_LeavesOrdinaryTelemetryAlone()
    {
        var redactor = new EvidenceRedactor();
        const string text = """{"detectedAt":"2026-11-04T14:05:00Z","latencyMs":4200,"requests":1234567,"opId":"6f9619ff-8b86-d011-b42d-00c04fd430c8","card":"4111 1111 1111 1112"}""";

        var result = redactor.Redact(text, Profile("acme", new PrivacySettings()));

        Assert.Equal(text, result.Text);
        Assert.Empty(result.CountsByPattern);
    }

    [Fact]
    public void Redactor_KeepsVersionStringsButMasksClientIp()
    {
        var redactor = new EvidenceRedactor();
        const string text =
            """{"timestamp":"2026-11-04T14:05:00.123Z","operation_Id":"4bf92f3577b34da6a3ce929d0e0e4736","id":"|4bf92f35.00f067aa0ba902b7","duration":1234.56,"resultCode":"500","application_Version":"1.0.0.0","appVersion":"2.4.1.17","sdkVersion":"dotnetc:2.21.0-429","client_IP":"203.0.113.7","cloud_RoleInstance":"web-01"}""" + "\n" +
            """{"type":"System.IO.FileNotFoundException","outerMessage":"Could not load file or assembly 'Contoso.Payments, Version=8.0.0.0, Culture=neutral, PublicKeyToken=null'","itemId":"6f9619ff-8b86-d011-b42d-00c04fd430c8"}""" + "\n" +
            """{"dependency":"SQL","target":"sql-prod.database.windows.net","durationMs":87,"success":true}""" + "\n" +
            """{"recentChange":"release 2026.11.04.2 rolled out","buildVersion":"10.2.3.4","note":"version 1.2.3.4 is live","raw":"{\"appVersion\":\"3.1.0.9\"}"}""" + "\n" +
            "application_Version=5.6.7.8 client_IP=198.51.100.23";

        var result = redactor.Redact(text, Profile("acme", new PrivacySettings()));

        var expected = text
            .Replace("203.0.113.7", "[REDACTED:ipv4]", StringComparison.Ordinal)
            .Replace("198.51.100.23", "[REDACTED:ipv4]", StringComparison.Ordinal);
        Assert.Equal(expected, result.Text);
        Assert.Equal(2, Assert.Single(result.CountsByPattern, kv => kv.Key == EvidenceRedactor.Keys.Ipv4).Value);
        Assert.Single(result.CountsByPattern);
    }

    [Theory]
    [InlineData("MemberId = 'Jane Doe'", "MemberId = '[REDACTED:pii]'")]
    [InlineData("""{"MemberId":"Jane Doe","x":1}""", """{"MemberId":"[REDACTED:pii]","x":1}""")]
    [InlineData("MemberId=Jane Doe;next=1", "MemberId=[REDACTED:pii];next=1")]
    [InlineData("MemberId: Jane Doe", "MemberId: [REDACTED:pii]")]
    [InlineData("MemberId=Jane Doe | stage=pay", "MemberId=[REDACTED:pii] | stage=pay")]
    [InlineData("{MemberId=Jane Doe}", "{MemberId=[REDACTED:pii]}")]
    public void Redactor_MasksWholeDimensionValues(string text, string expected)
    {
        var result = new EvidenceRedactor().Redact(text, Profile("acme", new PrivacySettings(PiiDimensions: ["MemberId"])));

        Assert.Equal(expected, result.Text);
        Assert.DoesNotContain("Jane", result.Text);
        Assert.DoesNotContain("Doe", result.Text);
        Assert.Equal(1, result.CountsByPattern["pii:MemberId"]);
    }

    [Fact]
    public void Redactor_PatternTimeout_FailsClosed()
    {
        var text = new string('a', 40_000) + "!";
        var profile = Profile("acme", new PrivacySettings(Patterns: ["(a+)+$"]));

        var result = new EvidenceRedactor().Redact(text, profile);

        Assert.Equal(EvidenceRedactor.UnprocessableToken, result.Text);
        Assert.Equal(1, result.CountsByPattern[EvidenceRedactor.Keys.Unprocessable]);
        Assert.Single(result.CountsByPattern);
    }

    [Fact]
    public void Redactor_MasksNonAsciiEmail()
    {
        var result = new EvidenceRedactor().Redact("from José.Pérez@exämple.com", Profile("acme", new PrivacySettings()));

        Assert.Equal("from [REDACTED:email]", result.Text);
    }

    [Fact]
    public void Redactor_MasksProfilePiiDimensions()
    {
        var redactor = new EvidenceRedactor();
        var profile = Profile("acme", new PrivacySettings(
            PiiDimensions: ["MemberId", "FullName"],
            Patterns: [@"\bMBR-\d+\b"]));
        var text =
            """{"customDimensions":{"MemberId":"A-1","FullName":"Jane Doe","Stage":"checkout"}}""" + "\n" +
            """{"raw":"{\"FullName\":\"John Roe\"}"}""" + "\n" +
            "query: memberid=A-2&stage=pay; also see " + MemberId;

        var result = redactor.Redact(text, profile);

        Assert.DoesNotContain("A-1", result.Text);
        Assert.DoesNotContain("Jane Doe", result.Text);
        Assert.DoesNotContain("John Roe", result.Text);
        Assert.DoesNotContain("A-2", result.Text);
        Assert.DoesNotContain(MemberId, result.Text);
        Assert.Contains("\"Stage\":\"checkout\"", result.Text);
        Assert.Contains("\"MemberId\":\"[REDACTED:pii]\"", result.Text);
        Assert.Contains("[REDACTED:pattern:0]", result.Text);
        Assert.Equal(2, result.CountsByPattern["pii:MemberId"]);
        Assert.Equal(2, result.CountsByPattern["pii:FullName"]);
        Assert.Equal(1, result.CountsByPattern["pattern:0"]);

        // Redacting already-redacted text is a no-op: tokens never match again.
        var again = redactor.Redact(result.Text, profile);
        Assert.Equal(result.Text, again.Text);
        Assert.Empty(again.CountsByPattern);
    }

    [Fact]
    public void NewProfile_DefaultsToNoRawEvidence()
    {
        var redactor = new EvidenceRedactor();
        var text = $"contact {Email}";

        // A new profile without a Privacy block is closed by default.
        Assert.DoesNotContain(Email, redactor.Redact(text, Profile("acme", privacy: null)).Text);
        Assert.True(EvidenceRedactor.RedactionRequired(Profile("acme", privacy: null)));
        Assert.False(new PrivacySettings().AllowRawEvidence);

        // The placeholder used when no profile row exists is closed as well.
        Assert.DoesNotContain(Email, redactor.Redact(text, EvidenceRedactor.PlaceholderProfile("acme")).Text);

        // Opting in to raw evidence is explicit.
        Assert.Equal(text, redactor.Redact(text, Profile("acme", new PrivacySettings(AllowRawEvidence: true))).Text);

        // The incumbent without a Privacy block keeps its behaviour; with one, it is closed too.
        Assert.Equal(text, redactor.Redact(text, Profile(KpiProfiles.Default, privacy: null)).Text);
        Assert.Equal(text, redactor.Redact(text, EvidenceRedactor.PlaceholderProfile(null)).Text);
        Assert.DoesNotContain(Email, redactor.Redact(text, Profile(KpiProfiles.Default, new PrivacySettings())).Text);
    }

    [Fact]
    public void Validator_RejectsInvalidPrivacyPatterns()
    {
        var profile = Profile("acme", new PrivacySettings(PiiDimensions: ["MemberId", " "], Patterns: ["(unclosed", "a*", @"\d{4}"]));

        var findings = CompanyProfileValidator.Validate(profile);

        Assert.Contains(findings, f => f.Code == "privacy-pattern-invalid" && f.Path == "$.privacy.patterns[0]");
        Assert.Contains(findings, f => f.Code == "privacy-pattern-matches-empty" && f.Path == "$.privacy.patterns[1]");
        Assert.DoesNotContain(findings, f => f.Path == "$.privacy.patterns[2]");
        Assert.Contains(findings, f => f.Code == "privacy-dimension-missing" && f.Path == "$.privacy.piiDimensions[1]");
    }

    [Fact]
    public async Task Triage_PromptIsRedacted()
    {
        var (llm, _) = await RunTriageAsync("acme", new PrivacySettings(PiiDimensions: ["MemberId"]));

        var prompt = Assert.Single(llm.Prompts);
        Assert.DoesNotContain(Email, prompt);
        Assert.DoesNotContain("4111 1111", prompt);
        Assert.DoesNotContain("41111111", prompt);
        Assert.DoesNotContain(MemberId, prompt);
        Assert.Contains("[REDACTED:email]", prompt);
        Assert.Contains("[REDACTED:card]", prompt);
        Assert.Contains("[REDACTED:pii]", prompt);
        Assert.Contains("SIGNAL", prompt);
    }

    [Fact]
    public async Task Triage_IncumbentWithoutPrivacy_KeepsRawEvidence()
    {
        var (llm, _) = await RunTriageAsync(KpiProfiles.Default, privacy: null);

        var prompt = Assert.Single(llm.Prompts);
        Assert.Contains(Email, prompt);
        Assert.Contains(MemberId, prompt);
    }

    [Fact]
    public async Task Inspector_PromptIsRedacted()
    {
        var store = new InMemoryCompanyProfileStore([Profile("acme", new PrivacySettings(PiiDimensions: ["MemberId"]))]);
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance, new EvidenceRedactor(), store);
        var llm = new CapturingToolLlm();
        var forensicCase = Case("acme");
        IReadOnlyList<LlmMessage> messages =
        [
            new("system", "You are the Planner."),
            new("user", $"Facts: {SensitiveEvidence()}"),
            new("tool", $"{{\"MemberId\":\"{MemberId}\",\"email\":\"{Email}\"}}", ToolCallId: "call-1"),
        ];

        await recorder.CallAsync(forensicCase, llm, "model-a", "Planner", AgentKind.Planner, messages, [], 400, CancellationToken.None);

        var sentMessages = Assert.Single(llm.Calls);
        var sent = string.Join("\n", sentMessages.Select(m => m.Content));
        Assert.DoesNotContain(Email, sent);
        Assert.DoesNotContain("4111 1111", sent);
        Assert.DoesNotContain("41111111", sent);
        Assert.DoesNotContain(MemberId, sent);
        Assert.Contains("You are the Planner.", sent);
        Assert.Equal("call-1", sentMessages[2].ToolCallId);

        // The persisted turn is the redacted exchange, i.e. what was actually sent.
        var turn = Assert.Single(await transcripts.GetTurnsAsync(forensicCase.Id));
        Assert.DoesNotContain(Email, turn.PromptText);
        Assert.DoesNotContain(MemberId, turn.PromptText);
    }

    [Fact]
    public async Task Inspector_IncumbentCase_SendsMessagesUnchanged()
    {
        var recorder = new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance, new EvidenceRedactor());
        var llm = new CapturingToolLlm();
        IReadOnlyList<LlmMessage> messages = [new("user", $"Facts: {SensitiveEvidence()}")];

        await recorder.CallAsync(Case(""), llm, "model-a", "Planner", AgentKind.Planner, messages, [], 400, CancellationToken.None);

        Assert.Same(messages, Assert.Single(llm.Calls));
    }

    [Fact]
    public async Task Redaction_AuditStoresCountsNotValues()
    {
        var logger = new CollectingLogger<TriageMonkeyService>();
        var (_, activity) = await RunTriageAsync("acme", new PrivacySettings(PiiDimensions: ["MemberId"]), logger);

        var completed = Assert.Single(activity.Entries, e => e.Kind == DaemonActivityKinds.InvestigationCompleted);
        using var data = JsonDocument.Parse(completed.DataJson!);
        var redactions = data.RootElement.GetProperty("redactions");
        Assert.True(redactions.GetProperty(EvidenceRedactor.Keys.Email).GetInt32() >= 1);
        Assert.True(redactions.GetProperty(EvidenceRedactor.Keys.Card).GetInt32() >= 1);
        Assert.True(redactions.GetProperty("pii:MemberId").GetInt32() >= 1);

        var everything = string.Join("\n", activity.Entries.Select(e => e.Message + " " + e.DataJson)) + "\n" + string.Join("\n", logger.Messages);
        Assert.DoesNotContain(Email, everything);
        Assert.DoesNotContain("4111 1111", everything);
        Assert.DoesNotContain("41111111", everything);
        Assert.DoesNotContain(MemberId, everything);
        Assert.Contains(logger.Messages, m => m.Contains("email=", StringComparison.Ordinal));

        // The Inspector keeps the same audit per case: names and counts only.
        var recorder = new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance, new EvidenceRedactor());
        var forensicCase = Case("acme");
        await recorder.CallAsync(forensicCase, new CapturingToolLlm(), "model-a", "Planner", AgentKind.Planner,
            [new LlmMessage("user", $"{Email} and {Email}")], [], 400, CancellationToken.None);
        var audit = recorder.RedactionsFor(forensicCase.Id);
        Assert.Equal(2, audit[EvidenceRedactor.Keys.Email]);
        Assert.DoesNotContain(audit, kv => kv.Key.Contains(Email, StringComparison.Ordinal));
    }

    private static string SensitiveEvidence() =>
        $"customer {Email} paid with {Card}, MemberId={MemberId}";

    private static async Task<(A5FakeLlmClient Llm, RecordingActivity Activity)> RunTriageAsync(
        string profileKey, PrivacySettings? privacy, ILogger<TriageMonkeyService>? logger = null)
    {
        var store = new A5InMemoryTriageStore();
        var signal = A5TriageFixtures.Signal() with
        {
            ProfileKey = profileKey,
            Detection = $"Latency doubled for {Email}",
        };
        var investigation = A5TriageFixtures.Queued(signal: signal);
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        var activity = new RecordingActivity();
        var profiles = new InMemoryCompanyProfileStore([Profile(profileKey, privacy)]);
        IEvidenceProvider[] providers = [new SensitiveEvidenceProvider(TriageDimensions.ApplicationHealth)];

        var service = new TriageMonkeyService(
            store,
            providers,
            llm,
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            logger ?? NullLogger<TriageMonkeyService>.Instance,
            activity: activity,
            profiles: profiles,
            redactor: new EvidenceRedactor());

        await service.InvestigateAsync(investigation.InvestigationId, CancellationToken.None);
        return (llm, activity);
    }

    private static ForensicCase Case(string profileKey) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        ProfileKey = profileKey,
        CaseId = "case-1",
        EntityKey = "club:1",
        Vertical = "hotels",
        Stage = "checkout",
    };

    private static CompanyProfile Profile(string key, PrivacySettings? privacy) => new(
        key,
        key,
        new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        [],
        [],
        new CompanyGlossary("service", "flow", "step", "entity", "owner"),
        Privacy: privacy);

    private sealed class SensitiveEvidenceProvider(string dimension) : IEvidenceProvider
    {
        public string Dimension => dimension;

        public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
        {
            var json = JsonSerializer.SerializeToElement(new
            {
                exception = $"Payment failed for {Email} with card {Card}",
                customDimensions = new Dictionary<string, string> { ["MemberId"] = MemberId, ["Stage"] = "checkout" },
            });
            return Task.FromResult(new EvidenceSection(dimension, true, json, null));
        }
    }

    private sealed class CapturingToolLlm : IToolCallingLlmClient
    {
        public List<IReadOnlyList<LlmMessage>> Calls { get; } = [];

        public Task<LlmCompletion> CompleteAsync(string model, IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, int maxTokens, CancellationToken ct = default)
        {
            Calls.Add(messages);
            return Task.FromResult(new LlmCompletion("{}", [], "stop", 10, 2, model, null));
        }
    }

    private sealed class RecordingActivity : IDaemonActivityRecorder
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public List<(string Kind, string Message, string? DataJson)> Entries { get; } = [];

        public IDaemonRun BeginRun(string daemonKey) => new Run(this, daemonKey);

        private sealed class Run(RecordingActivity owner, string daemonKey) : IDaemonRun
        {
            public Guid RunId { get; } = Guid.NewGuid();
            public string DaemonKey { get; } = daemonKey;
            public void Info(string kind, string message, object? data = null) => Add(kind, message, data);
            public void Warning(string kind, string message, object? data = null) => Add(kind, message, data);
            public void Error(string kind, string message, object? data = null) => Add(kind, message, data);
            public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

            private void Add(string kind, string message, object? data) =>
                owner.Entries.Add((kind, message, data is null ? null : JsonSerializer.Serialize(data, Web)));
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
