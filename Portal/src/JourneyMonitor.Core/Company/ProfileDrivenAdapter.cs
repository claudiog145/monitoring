using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Company;

/// <summary>
/// Answers every <see cref="ICompanyAdapter"/> question from a <see cref="CompanyProfile"/>
/// document and from nothing else. There is no company-specific name, host, flow or step anywhere
/// in this file on purpose: the moment one appears, the profile has stopped being the source of
/// truth and the seam is decorative.
///
/// Evaluation order mirrors the generated KQL <c>case()</c> exactly - steps by
/// <see cref="StepDefinition.Order"/>, matchers in list order, first hit wins, service fallback
/// last. The two have to agree row for row: the board is rendered from the KQL and the classifier
/// runs here, so a disagreement would not be an error, it would be two different stories about the
/// same request, which is the failure this whole seam exists to remove.
///
/// Never throws. An input it cannot classify comes back as <see cref="StepMatch.NotResolved"/>;
/// only a defect in this class produces <see cref="StepMatch.Unavailable"/>, which is kept
/// distinct because one is a mapping gap for whoever onboarded the company and the other is ours.
/// </summary>
public sealed class ProfileDrivenAdapter : ICompanyAdapter
{
    /// <summary>Admin-editable patterns are untrusted input; a bounded match cannot be turned into a CPU denial of service.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly CompanyProfile _profile;
    private readonly ILogger? _logger;

    /// <summary>Compiling per call would dominate the cost of classifying a board.</summary>
    private readonly ConcurrentDictionary<(string Pattern, bool CaseSensitive), Regex?> _regexCache = new();

    private readonly ConcurrentDictionary<string, byte> _unknownMatcherKinds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every step of every service, flattened and pre-ordered, because the matched step is what names the service.</summary>
    private readonly IReadOnlyList<OrderedStep> _orderedSteps;

    public ProfileDrivenAdapter(CompanyProfile profile, ILogger? logger = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _logger = logger;

        _orderedSteps = profile.Services
            .SelectMany(service => service.Flows
                .SelectMany(flow => flow.Steps.Select(step => new OrderedStep(service, flow, step))))
            // OrderBy is stable, so equal Order values keep the sequence the profile declared them in -
            // the same tie-break the generated case() gets by being written out in that sequence.
            .OrderBy(entry => entry.Step.Order)
            .ToList();
    }

    public string ProfileKey => _profile.ProfileKey;

    public CompanyLabels Labels => _profile.Labels;

    public CompanyGlossary Glossary => _profile.Glossary;

    public IReadOnlyList<ServiceDefinition> Services => _profile.Services;

    /// <summary>
    /// Matcher kinds seen in the profile that this build cannot evaluate. Exposed rather than only
    /// logged so a health endpoint or a test can assert on it: a matcher that quietly never fires
    /// looks exactly like traffic that never happened.
    /// </summary>
    public IReadOnlyCollection<string> UnknownMatcherKinds => _unknownMatcherKinds.Keys.ToArray();

    public StepMatch ResolveStep(TelemetryRowContext row)
    {
        if (row is null) return StepMatch.NotResolved();

        try
        {
            foreach (var candidate in _orderedSteps)
            {
                if (!ServeableSource(candidate.Service, row) || !ServeableHost(candidate.Service, row)) continue;

                foreach (var matcher in candidate.Step.Matchers)
                {
                    if (!Matches(matcher, row)) continue;

                    // The rule that recognised the row is what names the service. Nothing had to decide
                    // the service up front, which is why two product areas can share one host.
                    return StepMatch.Resolved(
                        candidate.Service.Key,
                        candidate.Flow.Key,
                        candidate.Step.Key,
                        CompanyResolution.PatternMatch);
                }
            }

            return Fallback(row);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Company:{ProfileKey}] step resolution failed; reporting the step as unavailable.", _profile.ProfileKey);
            return StepMatch.Unavailable();
        }
    }

    /// <summary>
    /// A catch-all is still an answer, but a weaker one, and the caller has to see the difference -
    /// otherwise a profile that maps nothing looks as healthy as one that maps everything. Two
    /// eligible catch-alls resolve to none: picking either would hide a defect in the profile.
    /// </summary>
    private StepMatch Fallback(TelemetryRowContext row)
    {
        ServiceDefinition? only = null;
        foreach (var service in _profile.Services)
        {
            if (string.IsNullOrWhiteSpace(service.FallbackStep) || !ServeableSource(service, row) || !ServeableHost(service, row)) continue;

            if (only is not null)
            {
                _logger?.LogWarning(
                    "[Company:{ProfileKey}] more than one service offers a fallback step for this row; leaving it unresolved.",
                    _profile.ProfileKey);
                return StepMatch.NotResolved();
            }

            only = service;
        }

        return only is null
            ? StepMatch.NotResolved()
            : StepMatch.Resolved(only.Key, null, only.FallbackStep!.Trim(), CompanyResolution.FallbackWildcard);
    }

    /// <summary>
    /// Host filters NARROW a service, they do not identify it. No filters means the service accepts
    /// any host, which is the common case now that the matched step is what names the service.
    /// </summary>
    private static bool ServeableHost(ServiceDefinition service, TelemetryRowContext row)
    {
        if (service.HostFilters is null || service.HostFilters.Count == 0) return true;

        var host = row.Host;
        if (host is null) return false;

        return service.HostFilters.Any(filter => HostBelongs(host, filter));
    }

    /// <summary>
    /// A service only claims rows from the telemetry sources it declares. Without this, a broad
    /// marker belonging to one product area silently captures another's traffic - and a row
    /// labelled with the wrong product area is worse than an unmapped one, because it sends a
    /// named team to investigate an incident that is not theirs.
    ///
    /// A row whose origin is unknown narrows nothing: the source cannot prove the row does NOT
    /// belong, and dropping it would be a silence that reads as health.
    /// </summary>
    private static bool ServeableSource(ServiceDefinition service, TelemetryRowContext row)
    {
        if (service.TelemetrySourceAliases is null || service.TelemetrySourceAliases.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(row.SourceAlias)) return true;

        var observed = Normalize(row.SourceAlias);
        if (observed.Length == 0) return true;

        // The board labels its origin for humans ("Cruise Core") while the catalogue names it for
        // machines ("cruise-core"), so the comparison is on letters only and accepts either as a
        // substring of the other.
        return service.TelemetrySourceAliases.Any(alias =>
        {
            var declared = Normalize(alias);
            return declared.Length > 0 &&
                   (declared.Contains(observed, StringComparison.Ordinal) ||
                    observed.Contains(declared, StringComparison.Ordinal));
        });
    }

    private static string Normalize(string? value) =>
        value is null ? "" : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public EntityIdentity ResolveAffectedEntity(TelemetryRowContext row)
    {
        if (row is null) return EntityIdentity.NotResolved();

        try
        {
            foreach (var source in _profile.EntityIdentitySources.OrderBy(s => s.Order))
            {
                var value = Extract(source, row);
                if (string.IsNullOrWhiteSpace(value)) continue;

                return EntityIdentity.Resolved(
                    value.Trim(),
                    source.EntityKind,
                    SourceKeyOf(source),
                    CompanyResolution.ExactMatch);
            }

            return EntityIdentity.NotResolved();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Company:{ProfileKey}] entity resolution failed; reporting the entity as unavailable.", _profile.ProfileKey);
            return EntityIdentity.Unavailable();
        }
    }

    /// <summary>Reads one identity source off the row. An unknown kind yields nothing and is recorded, never silently ignored.</summary>
    private string? Extract(EntityIdentitySource source, TelemetryRowContext row)
    {
        switch (source.Kind?.Trim().ToLowerInvariant())
        {
            case EntityIdentitySourceKind.CustomDimension:
                return row.CustomDimension(source.Key);

            case EntityIdentitySourceKind.QueryParameter:
                return row.QueryParameter(source.Key);

            case EntityIdentitySourceKind.Host:
                return row.Host;

            case EntityIdentitySourceKind.RoleName:
                return row.RoleName;

            default:
                NoteUnknownKind($"identity:{source.Kind}");
                return null;
        }
    }
    /// <summary>Host-derived sources carry no field name, so the kind is the most specific thing left to report.</summary>
    private static string SourceKeyOf(EntityIdentitySource source) =>
        string.IsNullOrWhiteSpace(source.Key) ? source.Kind : source.Key.Trim();

    private static bool HostBelongs(string host, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return false;

        var f = filter.Trim().TrimStart('*', '.');
        if (f.Length == 0) return false;

        return host.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + f, StringComparison.OrdinalIgnoreCase);
    }

    private bool Matches(StepMatcher matcher, TelemetryRowContext row)
    {
        if (matcher is null || string.IsNullOrEmpty(matcher.Value)) return false;

        var comparison = matcher.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        switch (matcher.Kind?.Trim().ToLowerInvariant())
        {
            case StepMatcherKind.UrlContains:
                return row.UrlText.Contains(matcher.Value, comparison);

            case StepMatcherKind.PathContains:
                return row.PathText.Contains(matcher.Value, comparison);

            case StepMatcherKind.OperationEquals:
                return row.OperationText.Trim().Equals(matcher.Value.Trim(), comparison);

            case StepMatcherKind.OperationContains:
                return row.OperationText.Contains(matcher.Value, comparison);

            case StepMatcherKind.PageNameContains:
                return row.PageNameText.Contains(matcher.Value, comparison);

            case StepMatcherKind.Regex:
                return RegexMatches(matcher, row);

            default:
                NoteUnknownKind(matcher.Kind);
                return false;
        }
    }

    private bool RegexMatches(StepMatcher matcher, TelemetryRowContext row)
    {
        var regex = _regexCache.GetOrAdd((matcher.Value, matcher.CaseSensitive), key =>
        {
            try
            {
                var options = RegexOptions.CultureInvariant | (key.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new Regex(key.Pattern, options, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

        if (regex is null)
        {
            NoteUnknownKind($"{StepMatcherKind.Regex}:invalid-pattern");
            return false;
        }

        try
        {
            // Which field the pattern is written against is not part of the profile, so all three are
            // offered; a pattern anchored to one of them simply never matches the others.
            return regex.IsMatch(row.UrlText) || regex.IsMatch(row.OperationText) || regex.IsMatch(row.PageNameText);
        }
        catch (RegexMatchTimeoutException)
        {
            NoteUnknownKind($"{StepMatcherKind.Regex}:timeout");
            return false;
        }
    }

    private void NoteUnknownKind(string? kind)
    {
        var name = string.IsNullOrWhiteSpace(kind) ? "(blank)" : kind.Trim();
        if (_unknownMatcherKinds.TryAdd(name, 0))
        {
            _logger?.LogWarning(
                "[Company:{ProfileKey}] matcher kind \"{Kind}\" cannot be evaluated by this build; it will never match.",
                _profile.ProfileKey, name);
        }
    }

    private readonly record struct OrderedStep(ServiceDefinition Service, FlowDefinition Flow, StepDefinition Step);
}
