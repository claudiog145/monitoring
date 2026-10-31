using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Onboarding;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Onboarding;

/// <summary>
/// What a draft is allowed to say, and - mostly - what it must refuse to say.
///
/// The assertions that matter here are the negative ones. A draft that proposed a plausible
/// booking funnel would pass a careless review and then quietly misclassify every row for months,
/// so "contains no step" and "contains no name this code made up" are pinned as hard as the
/// ordering is.
/// </summary>
public sealed class ProfileDraftBuilderTests
{
    [Fact]
    public void Build_ProposesTheHighestCoverageKeyFirst_WithItsCoverageAttached()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates:
            [
                Candidate("WeakId", coverage: 11.5),
                Candidate("TenantId", coverage: 92.25),
                Candidate("MiddlingId", coverage: 64.0),
            ]));

        Assert.Equal(
            new[] { "TenantId", "MiddlingId", "WeakId" },
            draft.Profile.EntityIdentitySources.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, draft.Profile.EntityIdentitySources.Select(s => s.Order).ToArray());
        Assert.Equal(EntityIdentitySourceKind.CustomDimension, draft.Profile.EntityIdentitySources[0].Kind);

        var proposal = Assert.Single(draft.Proposals.Where(p => p.Value == "TenantId"));
        Assert.True(proposal.Proposed);
        Assert.Equal("entityIdentitySources[0]", proposal.Target);
        Assert.Equal(92.25, proposal.CoveragePercent!.Value);
        Assert.Contains(proposal.Evidence, e => e.Contains("92.25", StringComparison.Ordinal));

        Assert.True(draft.CanAttributeIncidents);
        Assert.Equal(DraftReadiness.NeedsReview, draft.Readiness);
    }

    [Fact]
    public void Build_WhenEveryCandidateIsLowCoverage_SaysTheEnterpriseCannotAttributeIncidents()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates:
            [
                Candidate("SomeId", coverage: 7.0),
                Candidate("OtherId", coverage: 3.25),
            ]));

        var blocker = Assert.Single(draft.Warnings.Where(w => w.Code == DraftWarningCode.CannotAttributeIncidents));
        Assert.Equal(DraftWarningSeverity.Blocker, blocker.Severity);
        Assert.Contains("cannot attribute", blocker.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7", blocker.Message, StringComparison.Ordinal);

        Assert.False(draft.CanAttributeIncidents);
        Assert.Equal(DraftReadiness.Blocked, draft.Readiness);
        Assert.Equal(DraftWarningSeverity.Blocker, draft.Warnings[0].Severity);
    }

    [Fact]
    public void Build_WhenNothingWasMeasured_SaysTheEnterpriseCannotAttributeIncidents()
    {
        var draft = ProfileDraftBuilder.Build(Report(candidates: []));

        var blocker = Assert.Single(draft.Warnings.Where(w => w.Code == DraftWarningCode.CannotAttributeIncidents));
        Assert.Contains("cannot attribute", blocker.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no customDimensions key was measured", blocker.Message, StringComparison.Ordinal);
        Assert.Empty(draft.Profile.EntityIdentitySources);
        Assert.False(draft.CanAttributeIncidents);
    }

    [Fact]
    public void Build_InventsNoStepAndNoProductName()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            roleNames: [new ObservedRoleName("checkout-api", 4_100), new ObservedRoleName("search-api", 900)],
            hosts: [new ObservedHost("shop.example.test", 5_000)]));

        // Not one flow, not one step, anywhere.
        Assert.All(draft.Profile.Services, service => Assert.Empty(service.Flows));
        Assert.All(draft.Profile.Services, service => Assert.Null(service.FallbackStep));

        // Every name in the document traces back to something that was observed.
        string[] observed = ["checkout-api", "search-api", "shop.example.test"];
        Assert.All(draft.Profile.Services, service => Assert.Contains(service.DisplayName, observed));

        // No vocabulary borrowed from the platform's first customer.
        var json = JsonSerializer.Serialize(draft.Profile, Web);
        foreach (var arriviaWord in new[] { "vertical", "club", "cobrand", "hotel", "cruise", "booking", "checkout-funnel" })
        {
            Assert.DoesNotContain(arriviaWord, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(draft.Profile.Glossary.Description);
        Assert.Equal("Entity", draft.Profile.Labels.Entity);
        Assert.All(draft.Profile.EntityIdentitySources, s => Assert.Equal("unspecified", s.EntityKind));
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.NoStepsProposed);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.EntityKindUnspecified);

        // Saving a draft must not be the same decision as switching it on.
        Assert.False(draft.Profile.Enabled);
    }

    [Fact]
    public void Build_ProposesServicesFromRoleNames_AndLeavesHostsUnassigned()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            roleNames: [new ObservedRoleName("Checkout API", 4_100)],
            hosts: [new ObservedHost("shop.example.test", 5_000)]));

        var service = Assert.Single(draft.Profile.Services);
        Assert.Equal("Checkout API", service.DisplayName);
        Assert.Equal("checkout-api", service.Key);
        Assert.Equal(new[] { "contoso-prod" }, service.TelemetrySourceAliases.ToArray());
        Assert.Empty(service.HostFilters);

        var host = Assert.Single(draft.Proposals.Where(p => p.Kind == DraftProposalKind.HostFilter));
        Assert.False(host.Proposed);
        Assert.Equal("", host.Target);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.HostsNeedAssignment);
    }

    [Fact]
    public void Build_WithNoRoleNames_FallsBackToHostsAndFiltersOnThem()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            hosts: [new ObservedHost("shop.example.test", 5_000)]));

        var service = Assert.Single(draft.Profile.Services);
        Assert.Equal("shop.example.test", service.DisplayName);
        Assert.Equal(new[] { "shop.example.test" }, service.HostFilters.ToArray());
        Assert.Empty(service.Flows);
    }

    [Fact]
    public void Build_WithNothingObserved_ProposesNoServiceAndSaysSo()
    {
        var draft = ProfileDraftBuilder.Build(Report(candidates: [Candidate("TenantId", coverage: 88.0)]));

        Assert.Empty(draft.Profile.Services);
        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.NoServicesProposed);
    }

    [Fact]
    public void Build_RejectsFreeTextAndConstantKeys_ButStillReportsThemWithTheNumberThatRejectedThem()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates:
            [
                // A 100%-coverage message field would otherwise out-rank every real identifier.
                Candidate("ExceptionMessage", coverage: 100.0, distinct: 4_000, maxValueLength: 480),
                Candidate("Environment", coverage: 100.0, distinct: 1),
                Candidate("TenantId", coverage: 71.0),
            ]));

        Assert.Equal(new[] { "TenantId" }, draft.Profile.EntityIdentitySources.Select(s => s.Key).ToArray());

        var freeText = Assert.Single(draft.Proposals.Where(p => p.Value == "ExceptionMessage"));
        Assert.False(freeText.Proposed);
        Assert.Equal(100.0, freeText.CoveragePercent!.Value);
        Assert.Contains("prose rather than an identifier", freeText.Evidence[0], StringComparison.Ordinal);

        var constant = Assert.Single(draft.Proposals.Where(p => p.Value == "Environment"));
        Assert.False(constant.Proposed);
        Assert.Contains("constant", constant.Evidence[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_CollapsesTheSameKeyMeasuredOnTwoTables_IntoOneRule()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates:
            [
                Candidate("TenantId", coverage: 90.0, table: "requests"),
                Candidate("TenantId", coverage: 40.0, table: "pageViews"),
            ]));

        var source = Assert.Single(draft.Profile.EntityIdentitySources);
        Assert.Equal("TenantId", source.Key);

        var proposal = Assert.Single(draft.Proposals.Where(p => p.Kind == DraftProposalKind.EntityIdentitySource));
        Assert.Contains(proposal.Evidence, e => e.Contains("Also measured on pageViews", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CarriesTheDiscoveryWarningsIntoTheDraft()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            status: TelemetryProfileStatus.Degraded,
            reportWarnings: ["traces: customDimensions probe failed, so no key coverage was measured for it."]));

        Assert.Contains(draft.Warnings, w => w.Code == DraftWarningCode.DiscoveryDegraded);
        Assert.Contains(draft.Warnings, w =>
            w.Code == DraftWarningCode.DiscoveryNote &&
            w.Message.StartsWith("traces:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WhenDiscoveryNeverReachedTheResource_IsBlocked()
    {
        var report = Report(candidates: [], status: TelemetryProfileStatus.SourceUnresolved) with
        {
            Error = "No telemetry source registered for alias \"contoso-prod\".",
        };

        var draft = ProfileDraftBuilder.Build(report);

        Assert.Equal(DraftReadiness.Blocked, draft.Readiness);
        var blocker = Assert.Single(draft.Warnings.Where(w => w.Code == DraftWarningCode.SourceUnresolved));
        Assert.Contains("No telemetry source registered", blocker.Message, StringComparison.Ordinal);

        // An unreached resource must not also be reported as "this customer is unattributable":
        // the first is our plumbing, the second is their telemetry, and they are fixed by
        // different people.
        Assert.DoesNotContain(draft.Warnings, w => w.Code == DraftWarningCode.CannotAttributeIncidents);
    }

    /// <summary>
    /// PascalCase serialisation has silently produced nulls in this repo before, and a profile
    /// whose Services came back null would be accepted by an editor and throw inside a timer run.
    /// The round trip is asserted against a BEHAVIOUR - the adapter classifying a row - rather
    /// than against property equality, because that is what a usable profile means.
    /// </summary>
    [Fact]
    public void Draft_OnceAHumanAddsSteps_RoundTripsThroughWebJsonIntoAUsableProfile()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            roleNames: [new ObservedRoleName("Checkout API", 4_100)]));

        var reviewed = draft.Profile with
        {
            Enabled = true,
            Glossary = draft.Profile.Glossary with { Description = "Contoso sells widgets." },
            EntityIdentitySources = [draft.Profile.EntityIdentitySources[0] with { EntityKind = "tenant" }],
            Services =
            [
                draft.Profile.Services[0] with
                {
                    Flows =
                    [
                        new FlowDefinition("purchase", "Purchase",
                        [
                            new StepDefinition("01-cart", "01 Cart", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/cart")]),
                            new StepDefinition("02-pay", "02 Pay", 2, [new StepMatcher(StepMatcherKind.UrlContains, "/pay")]),
                        ]),
                    ],
                },
            ],
        };

        var round = JsonSerializer.Deserialize<CompanyProfile>(JsonSerializer.Serialize(reviewed, Web), Web);

        Assert.NotNull(round);
        Assert.NotNull(round!.Services);
        Assert.NotNull(round.Labels);
        Assert.NotNull(round.Glossary);
        Assert.NotNull(round.EntityIdentitySources);
        Assert.Equal("tenant", round.EntityIdentitySources[0].EntityKind);

        var match = new ProfileDrivenAdapter(round).ResolveStep(
            new TelemetryRowContext(Url: "https://shop.example.test/pay?x=1"));

        Assert.True(match.IsResolved);
        Assert.Equal("02-pay", match.Step);
        Assert.Equal("checkout-api", match.Service);

        // And the same document survives the validator the admin endpoint writes through.
        Assert.True(StructuralProfileWriteValidator.Instance.Validate(round).IsValid);
    }

    [Fact]
    public void Validator_AcceptsTheRawDraft_SoReviewWorkCanBeSavedBeforeItIsFinished()
    {
        var draft = ProfileDraftBuilder.Build(Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            roleNames: [new ObservedRoleName("Checkout API", 4_100)]));

        var result = StructuralProfileWriteValidator.Instance.Validate(draft.Profile);

        Assert.True(result.IsValid, string.Join(" | ", result.Problems));
    }

    [Theory]
    [InlineData("duplicate-service-key", "duplicates services[0].key")]
    [InlineData("unknown-matcher-kind", "is not a known matcher kind")]
    [InlineData("bad-regex", "is not a valid regular expression")]
    [InlineData("duplicate-step-order", "step order decides which matcher wins")]
    [InlineData("stepless-step", "can never match")]
    [InlineData("blank-entity-kind", "entityKind is required")]
    public void Validator_NamesTheProblem(string flavour, string expected)
    {
        var result = StructuralProfileWriteValidator.Instance.Validate(BrokenProfile(flavour));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Upsert_WithAnInvalidProfile_Returns400NamingTheProblem()
    {
        var store = new RecordingProfileStore();
        var response = await Admin(store, secret: "s3cret").UpsertAsync(
            Request("PUT", "https://localhost/api/settings/company-profiles/contoso", BrokenProfile("unknown-matcher-kind"))
                .WithHeader("X-Settings-Write-Secret", "s3cret"),
            "contoso",
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = response.ReadJson();
        Assert.Contains("is not a known matcher kind", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(json["problems"]!.AsArray());

        // The point of a 400 here: nothing reached the store, and nothing was defaulted for the caller.
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Upsert_WithMalformedJson_Returns400RatherThan500()
    {
        var response = await Admin(new RecordingProfileStore(), secret: "s3cret").UpsertAsync(
            Request("PUT", "https://localhost/api/settings/company-profiles/contoso", "{ not json")
                .WithHeader("X-Settings-Write-Secret", "s3cret"),
            "contoso",
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid JSON", response.ReadJson()["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upsert_WithNoSecretConfigured_FailsClosed()
    {
        var store = new RecordingProfileStore();
        var response = await Admin(store, secret: null).UpsertAsync(
            Request("PUT", "https://localhost/api/settings/company-profiles/contoso", ValidProfile()),
            "contoso",
            default);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Draft_WithNoSecretConfigured_FailsClosed()
    {
        var profiler = new ScriptedProfiler(_ => Report(candidates: [Candidate("TenantId", coverage: 88.0)]));

        var response = await Discovery(profiler, secret: null).DraftAsync(
            Request("POST", "https://localhost/api/onboarding/draft", new { alias = "contoso-prod" }),
            default);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(profiler.Requests);
    }

    [Fact]
    public async Task Draft_WithMalformedJson_Returns400WithAUsefulMessage()
    {
        var response = await Discovery(new ScriptedProfiler(_ => Report(candidates: [])), secret: "internal").DraftAsync(
            Request("POST", "https://localhost/api/onboarding/draft", "{ \"alias\": ")
                .WithHeader("X-Internal-Api-Secret", "internal"),
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid JSON", response.ReadJson()["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_WithoutAnAlias_Returns400()
    {
        var response = await Discovery(new ScriptedProfiler(_ => Report(candidates: [])), secret: "internal").DraftAsync(
            Request("POST", "https://localhost/api/onboarding/draft", new { environment = "production" })
                .WithHeader("X-Internal-Api-Secret", "internal"),
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("alias", response.ReadJson()["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_ReturnsTheDraftAndTheMeasurementBehindIt_AndPersistsNothing()
    {
        var profiler = new ScriptedProfiler(_ => Report(
            candidates: [Candidate("TenantId", coverage: 88.0)],
            roleNames: [new ObservedRoleName("Checkout API", 4_100)]));

        var response = await Discovery(profiler, secret: "internal").DraftAsync(
            Request("POST", "https://localhost/api/onboarding/draft", new { alias = "contoso-prod", profileKey = "contoso", displayName = "Contoso" })
                .WithHeader("X-Internal-Api-Secret", "internal"),
            default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = response.ReadJson();

        Assert.Equal("contoso", json["draft"]!["profile"]!["profileKey"]!.GetValue<string>());
        Assert.Equal("Contoso", json["draft"]!["profile"]!["displayName"]!.GetValue<string>());
        Assert.False(json["draft"]!["profile"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(json["draft"]!["profile"]!["services"]![0]!["flows"]!.AsArray());
        Assert.Equal("NeedsReview", json["draft"]!["readiness"]!.GetValue<string>());
        Assert.NotEmpty(json["draft"]!["warnings"]!.AsArray());
        Assert.NotNull(json["discovery"]);

        var request = Assert.Single(profiler.Requests);
        Assert.Equal("contoso-prod", request.Alias);
    }

    [Fact]
    public async Task Draft_WhenTheAliasDoesNotResolve_Returns400RatherThanADraftOfNothing()
    {
        var profiler = new ScriptedProfiler(_ => Report(candidates: [], status: TelemetryProfileStatus.SourceUnresolved) with
        {
            Error = "No telemetry source registered for alias \"nope\".",
        });

        var response = await Discovery(profiler, secret: "internal").DraftAsync(
            Request("POST", "https://localhost/api/onboarding/draft", new { alias = "nope" })
                .WithHeader("X-Internal-Api-Secret", "internal"),
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No telemetry source registered", response.ReadJson()["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // Fixtures. Candidates are produced by the REAL ranker rather than hand-built, so a test can
    // never assert against a score the shipping code would not have produced.
    // ----------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static EntityIdentityCandidate Candidate(
        string key,
        double coverage,
        long distinct = 40,
        int maxValueLength = 12,
        string table = "requests")
    {
        var nonEmpty = (long)Math.Round(coverage * 100);
        return EntityIdentityCandidateRanker.Score(table, new CustomDimensionProfile(
            Key: key,
            OccurrenceCount: nonEmpty,
            NonEmptyCount: nonEmpty,
            DistinctValueCount: distinct,
            MaxValueLength: maxValueLength,
            CoveragePercent: coverage,
            NonEmptyCoveragePercent: coverage));
    }

    private static TelemetryResourceProfile Report(
        IReadOnlyList<EntityIdentityCandidate> candidates,
        IReadOnlyList<ObservedRoleName>? roleNames = null,
        IReadOnlyList<ObservedHost>? hosts = null,
        TelemetryProfileStatus status = TelemetryProfileStatus.Complete,
        IReadOnlyList<string>? reportWarnings = null)
    {
        var to = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        return new TelemetryResourceProfile(
            Alias: "contoso-prod",
            Environment: "production",
            WindowFrom: to.AddHours(-24),
            WindowTo: to,
            WindowHours: 24,
            MaxSampledRows: 50_000,
            Status: status,
            Error: null,
            Tables: [],
            IdentityCandidates: candidates,
            Hosts: new TelemetryHostProfile(
                roleNames is { Count: > 0 } || hosts is { Count: > 0 } ? TelemetryProbeState.Ok : TelemetryProbeState.NoData,
                null,
                "union isfuzzy=true ...",
                hosts ?? [],
                roleNames ?? []),
            Warnings: reportWarnings ?? [],
            GeneratedAt: to);
    }

    private static CompanyProfile ValidProfile() => new(
        ProfileKey: "contoso",
        DisplayName: "Contoso",
        Labels: new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        Services:
        [
            new ServiceDefinition("checkout", "Checkout", ["contoso-prod"], [], [], null),
        ],
        EntityIdentitySources: [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant")],
        Glossary: new CompanyGlossary("service", "flow", "step", "entity", "owner", "Contoso sells widgets."));

    private static CompanyProfile BrokenProfile(string flavour)
    {
        var profile = ValidProfile();
        var step = new StepDefinition("01-cart", "01 Cart", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/cart")]);

        return flavour switch
        {
            "duplicate-service-key" => profile with
            {
                Services = [profile.Services[0], profile.Services[0] with { DisplayName = "Checkout (copy)" }],
            },
            "unknown-matcher-kind" => WithFlow(profile, [step with { Matchers = [new StepMatcher("url-starts-with", "/cart")] }]),
            "bad-regex" => WithFlow(profile, [step with { Matchers = [new StepMatcher(StepMatcherKind.Regex, "[unclosed")] }]),
            "duplicate-step-order" => WithFlow(profile, [step, step with { Key = "02-pay" }]),
            "stepless-step" => WithFlow(profile, [step with { Matchers = [] }]),
            "blank-entity-kind" => profile with
            {
                EntityIdentitySources = [profile.EntityIdentitySources[0] with { EntityKind = "  " }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(flavour), flavour, "Unknown fixture."),
        };
    }

    private static CompanyProfile WithFlow(CompanyProfile profile, IReadOnlyList<StepDefinition> steps) => profile with
    {
        Services = [profile.Services[0] with { Flows = [new FlowDefinition("purchase", "Purchase", steps)] }],
    };

    private static CompanyProfileAdminFunctions Admin(ICompanyProfileStore store, string? secret) =>
        new(store,
            Options.Create(new MonitoringOptions { SettingsWriteSecret = secret }),
            NullLogger<CompanyProfileAdminFunctions>.Instance);

    private static TelemetryDiscoveryFunctions Discovery(ITelemetryResourceProfiler profiler, string? secret) =>
        new(profiler,
            Options.Create(new MonitoringOptions { InternalApiSecret = secret }),
            NullLogger<TelemetryDiscoveryFunctions>.Instance);

    private static OnboardingHttpRequest Request(string method, string url, object body) =>
        Request(method, url, body as string ?? JsonSerializer.Serialize(body, Web));

    private static OnboardingHttpRequest Request(string method, string url, string body) =>
        new(method, url, body);

    private sealed class RecordingProfileStore : ICompanyProfileStore
    {
        public List<CompanyProfile> Saved { get; } = [];

        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default) =>
            Task.FromResult(Saved.FirstOrDefault(p => p.ProfileKey == profileKey));

        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanyProfile>>(Saved);

        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default)
        {
            Saved.Add(profile);
            return Task.FromResult(profile);
        }

        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedProfiler(Func<TelemetryProfileRequest, TelemetryResourceProfile> respond) : ITelemetryResourceProfiler
    {
        public List<TelemetryProfileRequest> Requests { get; } = [];

        public Task<TelemetryResourceProfile> ProfileAsync(TelemetryProfileRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}

/// <summary>
/// An <see cref="HttpRequestData"/> that can carry a method and a body, which the shared fake in
/// <c>Tests/Functions</c> cannot - it is GET-only with an empty stream, and these endpoints are
/// PUT and POST. Kept here rather than widening that one, which four other tests depend on.
/// </summary>
internal sealed class OnboardingHttpRequest : HttpRequestData
{
    public OnboardingHttpRequest(string method, string url, string body) : base(new OnboardingFunctionContext())
    {
        Method = method;
        Url = new Uri(url);
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    }

    public override Stream Body { get; }

    public override HttpHeadersCollection Headers { get; } = new();

    public override IReadOnlyCollection<IHttpCookie> Cookies => throw new NotSupportedException();

    public override Uri Url { get; }

    public override IEnumerable<ClaimsIdentity> Identities => [];

    public override string Method { get; }

    public override HttpResponseData CreateResponse() => new OnboardingHttpResponse(FunctionContext);

    public OnboardingHttpRequest WithHeader(string name, string value)
    {
        Headers.Add(name, value);
        return this;
    }
}

internal sealed class OnboardingHttpResponse(FunctionContext context) : HttpResponseData(context)
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public override HttpHeadersCollection Headers { get; set; } = new();

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies => throw new NotSupportedException();
}

internal sealed class OnboardingFunctionContext : FunctionContext
{
    public override string InvocationId { get; } = Guid.NewGuid().ToString();

    public override string FunctionId { get; } = "onboarding-test";

    public override TraceContext TraceContext => throw new NotSupportedException();

    public override BindingContext BindingContext => throw new NotSupportedException();

    public override RetryContext RetryContext => throw new NotSupportedException();

    public override IServiceProvider InstanceServices { get; set; } = null!;

    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw new NotSupportedException();
}

internal static class OnboardingResponseExtensions
{
    public static JsonNode ReadJson(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The response body was not JSON.");
    }
}
