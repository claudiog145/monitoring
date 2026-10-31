using System.Text;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Core.Company;

/// <summary>
/// One arm of the generated classifier cascade: the predicate that recognises a row, and what
/// recognising it means.
///
/// Exposed rather than kept inside the renderer because it is the only honest way to test the
/// generated KQL against <see cref="ProfileDrivenAdapter"/> without an Application Insights
/// cluster: the rules are the decision, the KQL text is one rendering of it.
/// </summary>
/// <param name="HostTerms">
/// Null when the service narrows by nothing and therefore accepts any host. An EMPTY list is not
/// the same thing: it means the service declared host filters and every one of them was blank, so
/// it can never claim a row - the same conclusion <c>ProfileDrivenAdapter.ServeableHost</c> reaches.
/// </param>
public sealed record ComposedRule(
    int Index,
    string ServiceKey,
    string? FlowKey,
    string StepKey,
    string ServiceDisplayName,
    string StepDisplayName,
    bool IsFallback,
    IReadOnlyList<StepMatcher> Matchers,
    IReadOnlyList<string>? HostTerms,
    string Predicate);

/// <summary>
/// Turns a <see cref="CompanyProfile"/> into the KQL fragments a board template leaves blank:
/// which service and step a row belongs to, who is affected, which hosts are in scope, and which
/// telemetry resources to read.
///
/// It COMPOSES a query for a company; it does not reproduce anyone's existing query. An earlier
/// version of this file aimed at byte-for-byte fidelity with one customer's hand-written board,
/// which measured the wrong thing - the question is whether a new customer can be onboarded as
/// configuration, not whether a legacy file can be regenerated.
///
/// Pure and synchronous on purpose. It runs on the same thread as the callers of
/// <see cref="KqlPlaceholders.AssertNoneLeft"/>, which is why its placeholders are not on that
/// method's pass-through list - unlike <c>{{SOURCE:}}</c> there is no later pass to catch a miss.
///
/// Everything emitted here ends up inside a query that runs against the customer's telemetry, so
/// every profile-supplied value goes through <see cref="Literal"/>. A profile is edited by whoever
/// onboards a customer; a display name containing a double quote must close as a string, not open
/// a new KQL clause.
///
/// Columns it assumes the template has already projected - this is the contract between the
/// fragments and company-scout-board.kql.template.txt:
///   <c>url</c> (raw, including the query string), <c>name</c> (operation), <c>PageName</c>,
///   <c>Host</c> (lowercased), <c>customDimensions</c>, <c>cloud_RoleName</c>.
/// Note <c>url</c> and not <c>Path</c>: <see cref="TelemetryRowContext.UrlText"/> is the full url,
/// and matching the generated KQL against a path-only, lowercased column would quietly disagree
/// with the adapter for every marker that lives in a query string.
/// </summary>
public static class CompanyKqlTemplater
{
    /// <summary>
    /// The alphabet <see cref="KqlSourceResolver.PlaceholderPattern"/> will actually match. An
    /// alias outside it renders a <c>{{SOURCE:...}}</c> that the resolver does not recognise, and
    /// <see cref="KqlPlaceholders.AssertNoneLeft"/> waves anything starting with <c>{{SOURCE:</c>
    /// through - so without this check the malformed placeholder reaches Azure as literal text.
    /// </summary>
    private static readonly Regex AliasPattern = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Fills every company-profile placeholder of <paramref name="template"/>; leaves the rest alone.</summary>
    public static string Render(string template, CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var text = template ?? "";
        var newLine = DetectNewLine(text);

        text = Fill(text, KqlPlaceholders.EntityIdentity, () => RenderEntityIdentity(profile));
        text = Fill(text, KqlPlaceholders.HostFilter, () => RenderHostFilter(profile));
        text = Fill(text, KqlPlaceholders.SourceUnion, () => RenderRequestSourceUnion(profile, newLine));
        text = Fill(text, KqlPlaceholders.ExceptionSourceUnion, () => RenderExceptionSourceUnion(profile, newLine));
        text = Fill(text, KqlPlaceholders.ServiceAndStepClassifier, () => RenderServiceAndStepClassifier(profile, newLine));

        return text;
    }

    /// <summary>
    /// A multi-line fragment has to use the newline the template itself uses, or a template checked
    /// out with LF ends up with CRLF blocks spliced into it.
    /// </summary>
    public static string DetectNewLine(string text) =>
        text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";

    private static string Fill(string text, string placeholder, Func<string> value) =>
        text.Contains(placeholder, StringComparison.Ordinal)
            ? text.Replace(placeholder, value(), StringComparison.Ordinal)
            : text;

    // ------------------------------------------------------------------ classifier

    /// <summary>
    /// Every step of every service, flattened and ordered exactly as
    /// <see cref="ProfileDrivenAdapter"/> flattens and orders them, with the services that offer a
    /// fallback appended last.
    ///
    /// The two have to be built the same way or they are two different stories about one request:
    /// steps by <see cref="StepDefinition.Order"/> through a STABLE sort, matchers in list order,
    /// first hit wins, fallback last and only when exactly one service offers one.
    ///
    /// A step whose matchers are all blank is dropped rather than emitted as a dead arm: the
    /// adapter already treats a blank matcher value as "never matches", so an arm for it would be
    /// noise that a reader has to re-derive is unreachable.
    /// </summary>
    public static IReadOnlyList<ComposedRule> BuildRules(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var rules = new List<ComposedRule>();

        var ordered = profile.Services
            .SelectMany(service => service.Flows
                .SelectMany(flow => flow.Steps.Select(step => (Service: service, Flow: flow, Step: step))))
            .OrderBy(entry => entry.Step.Order)
            .ToList();

        foreach (var entry in ordered)
        {
            var matchers = (entry.Step.Matchers ?? [])
                .Where(m => m is not null && !string.IsNullOrEmpty(m.Value))
                .ToList();
            if (matchers.Count == 0) continue;

            var hostTerms = HostTermsOf(entry.Service);
            var hostPredicate = HostPredicate(hostTerms);
            var matcherPredicate = AnyOf(matchers.Select(RenderMatcher));

            rules.Add(new ComposedRule(
                rules.Count,
                entry.Service.Key,
                entry.Flow.Key,
                entry.Step.Key,
                entry.Service.DisplayName,
                entry.Step.DisplayName,
                IsFallback: false,
                matchers,
                hostTerms,
                hostPredicate is null ? matcherPredicate : $"{hostPredicate} and {matcherPredicate}"));
        }

        foreach (var service in FallbackServices(profile))
        {
            var hostTerms = HostTermsOf(service);
            var hostPredicate = HostPredicate(hostTerms);
            var step = service.FallbackStep!.Trim();

            // FallbackCandidates is computed by the emitted block below. Two eligible catch-alls
            // resolve to none, because picking either would hide a defect in the profile - the same
            // judgement ProfileDrivenAdapter.Fallback makes.
            rules.Add(new ComposedRule(
                rules.Count,
                service.Key,
                null,
                step,
                service.DisplayName,
                step,
                IsFallback: true,
                [],
                hostTerms,
                hostPredicate is null ? "FallbackCandidates==1" : $"FallbackCandidates==1 and {hostPredicate}"));
        }

        return rules;
    }

    /// <summary>
    /// The block that fills <see cref="KqlPlaceholders.ServiceAndStepClassifier"/>: ONE predicate
    /// cascade, then pure lookups off the arm it chose.
    ///
    /// The lookups are not a second classifier - they carry no predicate of their own and cannot
    /// disagree with the cascade. That is the whole point: a profile where two services share a
    /// host is told apart by WHICH step matched, so there is nothing left for a service
    /// discriminator to decide.
    /// </summary>
    public static string RenderServiceAndStepClassifier(CompanyProfile profile, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(profile);

        var rules = BuildRules(profile);
        var sb = new StringBuilder();

        var fallbacks = FallbackServices(profile);
        if (fallbacks.Count > 0)
        {
            var terms = fallbacks.Select(service =>
            {
                var predicate = HostPredicate(HostTermsOf(service));
                return predicate is null ? "1" : $"iff({predicate},1,0)";
            });
            sb.Append("| extend FallbackCandidates=").Append(string.Join("+", terms)).Append(newLine);
        }

        if (rules.Count == 0)
        {
            // A profile that recognises nothing still has to produce a running board: every row
            // lands unmapped, which is visible, instead of the board failing to render at all.
            sb.Append("| extend MatchIndex=-1").Append(newLine);
            sb.Append("| extend ServiceKey=\"\",Service=\"\",StepKey=\"\",Step=\"\"");
            return sb.ToString();
        }

        sb.Append("| extend MatchIndex=case(").Append(newLine);
        foreach (var rule in rules)
        {
            sb.Append("    ").Append(rule.Predicate).Append(',').Append(rule.Index).Append(',').Append(newLine);
        }
        sb.Append("    -1)").Append(newLine);

        sb.Append("| extend ServiceKey=").Append(Lookup(rules, r => r.ServiceKey)).Append(newLine);
        sb.Append("| extend Service=").Append(Lookup(rules, r => r.ServiceDisplayName)).Append(newLine);
        sb.Append("| extend StepKey=").Append(Lookup(rules, r => r.StepKey)).Append(newLine);
        sb.Append("| extend Step=").Append(Lookup(rules, r => r.StepDisplayName));

        return sb.ToString();
    }

    private static IReadOnlyList<ServiceDefinition> FallbackServices(CompanyProfile profile) =>
        profile.Services.Where(s => !string.IsNullOrWhiteSpace(s.FallbackStep)).ToList();

    private static string Lookup(IReadOnlyList<ComposedRule> rules, Func<ComposedRule, string> value)
    {
        var sb = new StringBuilder("case(");
        foreach (var rule in rules)
        {
            sb.Append("MatchIndex==").Append(rule.Index).Append(',').Append(Literal(value(rule))).Append(',');
        }
        // Empty, not a word this renderer invented: the template owns what an unmapped row is called.
        sb.Append("\"\")");
        return sb.ToString();
    }

    /// <summary>
    /// One matcher as a KQL predicate.
    ///
    /// The kind is normalised the way <see cref="ProfileDrivenAdapter.ResolveStep"/> normalises it
    /// (trimmed, lower-cased) so that " URL-Contains " is not silently a different matcher here
    /// than it is there.
    /// </summary>
    public static string RenderMatcher(StepMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        var contains = matcher.CaseSensitive ? "contains_cs" : "contains";

        return matcher.Kind?.Trim().ToLowerInvariant() switch
        {
            StepMatcherKind.UrlContains => $"url {contains} {Literal(matcher.Value)}",
            // Path only, and lower-cased because TelemetryRowContext.Path lower-cases what the
            // adapter compares - a composed board that read the raw path would disagree with the
            // adapter on any url whose casing differs from the marker's.
            StepMatcherKind.PathContains =>
                $"tolower(tostring(parse_url(url).Path)) {contains} {Literal(matcher.Value)}",
            // The adapter compares the TRIMMED operation to the TRIMMED value, so the KQL has to trim too.
            StepMatcherKind.OperationEquals =>
                $"trim(@\"\\s+\",name) {(matcher.CaseSensitive ? "==" : "=~")} {Literal(matcher.Value.Trim())}",
            StepMatcherKind.OperationContains => $"name {contains} {Literal(matcher.Value)}",
            StepMatcherKind.PageNameContains => $"PageName {contains} {Literal(matcher.Value)}",
            StepMatcherKind.Regex => RenderRegex(matcher),
            _ => throw new InvalidOperationException(
                $"Unknown step matcher kind \"{matcher.Kind}\". Emitting nothing for it would quietly move " +
                "every row it should have claimed into the fallback step.")
        };
    }

    /// <summary>
    /// Which field a pattern is written against is not part of the profile, so all three are
    /// offered - the same choice the adapter makes. KQL has no case-insensitive form of
    /// <c>matches regex</c>; the inline flag is the only one.
    /// </summary>
    private static string RenderRegex(StepMatcher matcher)
    {
        var pattern = Literal(matcher.CaseSensitive ? matcher.Value : "(?i)" + matcher.Value);
        return $"(url matches regex {pattern} or name matches regex {pattern} or PageName matches regex {pattern})";
    }

    private static string AnyOf(IEnumerable<string> terms)
    {
        var list = terms.ToList();
        return list.Count == 1 ? list[0] : "(" + string.Join(" or ", list) + ")";
    }

    // ------------------------------------------------------------------ hosts

    /// <summary>
    /// The predicate for <c>| where {{HOST_FILTER}}</c>: a pre-filter that narrows the board to the
    /// hosts some service claims.
    ///
    /// It NEVER renders <c>false</c>, and it renders <c>true</c> as soon as one service accepts any
    /// host - because a service with no host filters does accept any host, so narrowing on behalf
    /// of the others would delete that service's traffic. A board filtered down to nothing is
    /// indistinguishable from a platform with no problems, which is the worst thing this fragment
    /// could do.
    /// </summary>
    public static string RenderHostFilter(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var predicates = new List<string>();
        foreach (var service in profile.Services)
        {
            var terms = HostTermsOf(service);
            if (terms is null) return "true";
            if (terms.Count == 0) continue;

            var predicate = HostPredicate(terms)!;
            if (!predicates.Contains(predicate, StringComparer.Ordinal)) predicates.Add(predicate);
        }

        return predicates.Count == 0 ? "true" : string.Join(" or ", predicates);
    }

    /// <summary>
    /// Null when the service declares no host filter at all. Mirrors
    /// <c>ProfileDrivenAdapter.HostBelongs</c>: a leading <c>*</c> or <c>.</c> is decoration, and a
    /// filter matches the host itself or any subdomain of it.
    /// </summary>
    private static IReadOnlyList<string>? HostTermsOf(ServiceDefinition service)
    {
        if (service.HostFilters is null || service.HostFilters.Count == 0) return null;

        return service.HostFilters
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().TrimStart('*', '.'))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? HostPredicate(IReadOnlyList<string>? terms)
    {
        if (terms is null) return null;
        // Declared host filters that all turned out blank: nothing can satisfy them.
        if (terms.Count == 0) return "false";

        var parts = terms.Select(t => $"Host =~ {Literal(t)} or Host endswith {Literal("." + t)}");
        return "(" + string.Join(" or ", parts) + ")";
    }

    // ------------------------------------------------------------------ entity identity

    /// <summary>
    /// <c>coalesce(...)</c> over <see cref="CompanyProfile.EntityIdentitySources"/> in
    /// <see cref="EntityIdentitySource.Order"/>. <c>OrderBy</c> is stable, so sources sharing an
    /// Order keep the order they were authored in rather than swapping between renders.
    /// </summary>
    public static string RenderEntityIdentity(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var parts = profile.EntityIdentitySources
            .OrderBy(s => s.Order)
            .Select(RenderIdentitySource)
            .ToList();

        // An empty literal, not a missing column: the template tests this with isnotempty() and has
        // a branch for an entity it could not identify.
        if (parts.Count == 0) return "\"\"";
        // coalesce() needs at least two arguments.
        if (parts.Count == 1) return parts[0];
        return "coalesce(" + string.Join(",", parts) + ")";
    }

    private static string RenderIdentitySource(EntityIdentitySource source) =>
        source.Kind?.Trim().ToLowerInvariant() switch
        {
            EntityIdentitySourceKind.CustomDimension => $"tostring(customDimensions{Member(source.Key)})",
            EntityIdentitySourceKind.QueryParameter => $"tostring(parse_url(url)[\"Query Parameters\"][{Literal(source.Key)}])",
            EntityIdentitySourceKind.Host => "Host",
            EntityIdentitySourceKind.RoleName => "cloud_RoleName",
            _ => throw new InvalidOperationException(
                $"Unknown entity identity source kind \"{source.Kind}\" (order {source.Order}). " +
                "A profile that names a kind this build cannot emit must fail here, not silently drop the identity.")
        };

    // ------------------------------------------------------------------ telemetry sources

    /// <summary>
    /// The aliases the profile's services declare, in declaration order, de-duplicated. Order comes
    /// from the profile and never from a hash set, so the same profile renders the same query.
    /// </summary>
    public static IReadOnlyList<string> SourceAliases(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var aliases = profile.Services
            .SelectMany(s => s.TelemetrySourceAliases ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var alias in aliases)
        {
            if (!AliasPattern.IsMatch(alias))
            {
                throw new InvalidOperationException(
                    $"Telemetry source alias \"{alias}\" is not resolvable: {{{{SOURCE:}}}} only accepts " +
                    "letters, digits, dot, underscore and dash. An alias outside that set would be left " +
                    "in the query as literal text instead of a resource id.");
            }
        }

        if (aliases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Company profile \"{profile.ProfileKey}\" declares no telemetry source alias, so there is " +
                "nothing for the board to read. An empty union would be a board that is always empty, which " +
                "reads exactly like a platform with no problems.");
        }

        return aliases;
    }

    /// <summary>One <c>app("{{SOURCE:alias}}").requests</c> branch per alias, projected to the columns the template uses.</summary>
    public static string RenderRequestSourceUnion(CompanyProfile profile, string newLine = "\r\n") =>
        string.Join("," + newLine, SourceAliases(profile).Select(alias =>
            $"(app(\"{{{{SOURCE:{alias}}}}}\").requests | where timestamp>=historyStart and timestamp<tEnd" +
            " | project timestamp,operation_Id,id,name,url,resultCode,success,duration,customDimensions," +
            "cloud_RoleName,cloud_RoleInstance,user_Id," +
            "AuthenticatedId=tostring(column_ifexists(\"user_AuthenticatedId\",\"\"))," +
            "SessionId=tostring(column_ifexists(\"session_Id\",\"\"))," +
            // Requests carry no page name of their own. column_ifexists keeps a page-name matcher
            // from being a query error on a source that has no such column - it simply never fires.
            "PageName=tostring(column_ifexists(\"PageName\",\"\"))" +
            $" | extend Source={Literal(alias)})"));

    /// <inheritdoc cref="RenderRequestSourceUnion"/>
    public static string RenderExceptionSourceUnion(CompanyProfile profile, string newLine = "\r\n") =>
        string.Join("," + newLine, SourceAliases(profile).Select(alias =>
            $"(app(\"{{{{SOURCE:{alias}}}}}\").exceptions | where timestamp>=historyStart and timestamp<tEnd" +
            $" | extend Source={Literal(alias)})"));

    // ------------------------------------------------------------------ primitives

    /// <summary>Dotted member access when the dimension name is an identifier, bracketed (and escaped) otherwise.</summary>
    private static string Member(string key) =>
        IsIdentifier(key) ? "." + key : $"[{Literal(key)}]";

    private static bool IsIdentifier(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!char.IsAsciiLetter(key[0]) && key[0] != '_') return false;
        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// A KQL double-quoted string literal. The whole reason a profile can be edited by an operator
    /// without becoming a query-injection vector.
    /// </summary>
    public static string Literal(string? value)
    {
        var v = value ?? "";
        var sb = new StringBuilder(v.Length + 2);
        sb.Append('"');
        foreach (var c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
