using System.Globalization;
using System.Text;
using DemoGenerator.Archetypes;

namespace DemoGenerator.Synthesis;

/// <summary>
/// Builds the <c>.set-or-append</c> commands that generate one app's telemetry for [from, to) INSIDE Kusto:
/// <c>range</c> over minutes, <c>mv-expand</c> to requests, and per-row pseudo-randomness from
/// <see cref="DeterministicRandom.KqlPrelude"/>. Same formulas and channel numbers as
/// <see cref="ReferenceSynthesizer"/>; a change in one must be made in the other.
/// </summary>
public static class KqlSynthesisBuilder
{
    public static readonly IReadOnlyList<string> Tables = ["requests", "dependencies", "exceptions", "traces", "pageViews", "customEvents"];

    private const string Client =
        "client_Type = \"PC\", client_Model = \"\", client_OS = \"\", client_IP = \"0.0.0.0\", client_City = \"\", " +
        "client_StateOrProvince = \"\", client_CountryOrRegion = country, client_Browser = \"\"";

    /// <summary>One command per table, in <see cref="Tables"/> order.</summary>
    public static IReadOnlyList<(string Table, string Command)> Commands(AppPlan app, IReadOnlyList<AppliedEffect> effects, DateTimeOffset from, DateTimeOffset to)
    {
        var relevant = Overlapping(effects, from, to);
        var baseQuery = BaseQuery(app, relevant, from, to);
        return Tables.Select(t => (t, $".set-or-append {t} <|\n{baseQuery}\n{Projection(app, relevant, t)}")).ToList();
    }

    /// <summary>Only effects that overlap the batch are rendered, which keeps every command short.</summary>
    private static List<AppliedEffect> Overlapping(IReadOnlyList<AppliedEffect> effects, DateTimeOffset from, DateTimeOffset to)
    {
        var start = SynthesisClock.FromEpochMinute(SynthesisClock.EpochMinute(from));
        var end = SynthesisClock.FromEpochMinute(SynthesisClock.EpochMinute(to));
        return effects.Where(e => e.Start < end && e.End > start).ToList();
    }

    public static string BaseQuery(AppPlan app, IReadOnlyList<AppliedEffect> allEffects, DateTimeOffset from, DateTimeOffset to)
    {
        var m0 = SynthesisClock.EpochMinute(from);
        var m1 = SynthesisClock.EpochMinute(to);
        var effects = Overlapping(allEffects, from, to);
        var steps = app.Steps;
        var deps = app.Dependencies;
        var exceptions = steps.SelectMany(s => s.Exceptions).ToList();

        var sb = new StringBuilder(8192);
        sb.Append(DeterministicRandom.KqlPrelude);
        Let(sb, "sName", steps.Select(s => Str(s.Name)));
        Let(sb, "sUrl", steps.Select(s => Str(s.Url)));
        Let(sb, "sMu", steps.Select(s => Num(s.Mu)));
        Let(sb, "sSg", steps.Select(s => Num(s.Sigma)));
        Let(sb, "sEr", steps.Select(s => Num(s.ErrorRate)));
        Let(sb, "sNd", steps.Select(s => s.Dependencies.Count.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sDo", steps.Select(s => s.FirstDependency.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sPv", steps.Select(s => s.EmitsPageView ? "true" : "false"));
        Let(sb, "sBe", steps.Select(s => Str(s.BusinessEvent ?? "")));
        Let(sb, "sBeS", steps.Select(s => s.BusinessEventOnSuccessOnly ? "true" : "false"));
        Let(sb, "dPartner", deps.Select(d => Str(d.Partner)));
        Let(sb, "dTarget", deps.Select(d => Str(d.Target)));
        Let(sb, "dType", deps.Select(d => Str(d.Type)));
        Let(sb, "dName", deps.Select(d => Str(d.Name)));
        Let(sb, "dMu", deps.Select(d => Num(d.Mu)));
        Let(sb, "dSg", deps.Select(d => Num(d.Sigma)));
        Let(sb, "dFr", deps.Select(d => Num(d.FailureRate)));
        Let(sb, "xType", exceptions.Select(x => Str(x.Type)));
        Let(sb, "xMsg", exceptions.Select(x => Str(x.Message)));
        Let(sb, "xMethod", exceptions.Select(x => Str(x.Method)));
        Let(sb, "ents", app.EntityIds.Select(Str));
        if (app.EntityHosts is not null) Let(sb, "hosts", app.EntityHosts.Select(Str));
        Let(sb, "rate", app.RatePerMinuteByHour.Select(Num));
        Let(sb, "countries", ReferenceSynthesizer.Countries.Select(Str));
        Let(sb, "sesG", steps.Select(s => s.SessionGroup.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sesP", steps.Select(s => s.SessionPosition.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sesS", ReferenceSynthesizer.SessionRankSlots(app).Select(s => s.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sesM", steps.Select(s => s.SessionLaneStride.ToString(CultureInfo.InvariantCulture)));
        Let(sb, "sesO", steps.Select(s => s.SessionLaneOffset.ToString(CultureInfo.InvariantCulture)));

        var c = CultureInfo.InvariantCulture;
        sb.Append("range m from ").Append(m0.ToString(c)).Append(" to ").Append((m1 - 1).ToString(c)).Append(" step 1\n");
        sb.Append("| extend mts = unixtime_seconds_todatetime(m * 60)\n");
        sb.Append("| extend r = todouble(rate[hourofday(mts)]) * iff(dayofweek(mts) == 0d or dayofweek(mts) == 6d, ")
          .Append(Num(app.WeekendFactor)).Append(", 1.0) * ")
          .Append(Product(effects.Where(e => e.IsGlobalTraffic), e => Num(e.Factor), _ => "")).Append('\n');
        sb.Append("| extend ms = mix(").Append(app.Seed.ToString(c)).Append(" + m)\n");
        sb.Append("| extend n = tolong(r) + iff(U(ms, ").Append(ReferenceSynthesizer.ChannelCount.ToString(c)).Append(") < r - todouble(tolong(r)), 1, 0)\n");
        sb.Append("| where n > 0\n");
        sb.Append("| mv-expand k = range(0, n - 1, 1) to typeof(long)\n");
        sb.Append("| extend rk = mix(ms + k)\n");
        sb.Append("| extend u1 = U(rk, 1), u2 = U(rk, 2)\n");
        sb.Append("| extend s = ").Append(CaseByThreshold("u1", steps.Select(st => st.CumulativeWeight).ToList())).Append('\n');
        sb.Append("| extend e = ").Append(EntityRankExpression(app)).Append('\n');
        // Session rank: position among the minute's requests of the same flow step (every entity), before
        // thinning (ReferenceSynthesizer.Session). Whole minutes are always in one command, so batching cannot change it.
        sb.Append("| extend sesK = tolong(sesS[s])\n");
        sb.Append("| sort by m asc, sesK asc, k asc\n");
        sb.Append("| extend sesRank = row_number(0, m != prev(m) or sesK != prev(sesK))\n");
        sb.Append("| extend sesAnchor = m - tolong(sesP[s])\n");
        sb.Append("| extend sesH = mix(mix(mix(").Append(app.SessionSeed.ToString(c))
          .Append(" + tolong(sesG[s])) + sesAnchor) + (sesRank * tolong(sesM[s]) + tolong(sesO[s])))\n");

        var thinning = effects.Where(e => e.IsThinning).ToList();
        if (thinning.Count > 0)
            sb.Append("| where U(rk, 7) < ").Append(Product(thinning, e => Num(e.Factor), _ => "")).Append('\n');

        sb.Append("| extend ts = mts + 1tick * tolong(U(rk, 0) * 600000000.0)\n");
        sb.Append("| extend z = sqrt(-2.0 * log(U(rk, 3))) * cos(2.0 * pi() * U(rk, 4))\n");
        sb.Append("| extend duration = exp(todouble(sMu[s]) + todouble(sSg[s]) * z) * ")
          .Append(Product(effects.Where(e => e.Kind == EffectKinds.LatencyMultiplier), e => Num(e.Factor), _ => "")).Append('\n');
        sb.Append("| extend nd = toint(sNd[s]), dOff = toint(sDo[s])\n");

        var maxDeps = app.MaxDependencies;
        var depFailures = effects.Where(e => e.Kind == EffectKinds.DependencyFailure).ToList();
        for (var j = 0; j < maxDeps; j++)
        {
            var dExpr = $"(dOff + {j})";
            sb.Append($"| extend f{j} = iff({j} < nd, U(rk, {ReferenceSynthesizer.ChannelDependency + 10 * j}) < 1.0 - (1.0 - todouble(dFr[dOff + {j}])) * ")
              .Append(Product(depFailures, e => Num(1.0 - e.Rate), e => $" and {dExpr} in ({Join(e.DependencyIndexes!)})"))
              .Append(", false)\n");
        }
        sb.Append("| extend mask = ").Append(maxDeps == 0 ? "0" : string.Join(" + ", Enumerable.Range(0, maxDeps).Select(j => $"iff(f{j}, {1 << j}, 0)"))).Append('\n');
        sb.Append("| extend fd = ").Append(maxDeps == 0 ? "-1" : "case(" + string.Join(", ", Enumerable.Range(0, maxDeps).Select(j => $"f{j}, dOff + {j}")) + ", -1)").Append('\n');
        sb.Append("| extend own = U(rk, 5) < 1.0 - (1.0 - todouble(sEr[s])) * ")
          .Append(Product(effects.Where(e => e.Kind == EffectKinds.ErrorRate), e => Num(1.0 - e.Rate), _ => "")).Append('\n');
        sb.Append("| extend success = not(own) and mask == 0\n");

        var typed = effects.Where(e => e.Kind == EffectKinds.ErrorRate && e.ExceptionType is not null).ToList();
        if (typed.Count == 0)
        {
            sb.Append("| extend scType = \"\", scMsg = \"\", scMethod = \"\"\n");
        }
        else
        {
            string Pick(Func<AppliedEffect, string> value) =>
                "case(" + string.Join(", ", typed.Select(e => $"{Predicate(e)}, {value(e)}")) + ", \"\")";
            string PerStep(AppliedEffect e, Func<PlannedStep, string> value) =>
                $"tostring(dynamic([{string.Join(", ", steps.Select(value))}])[s])";
            sb.Append("| extend scType = ").Append(Pick(e => Str(e.ExceptionType!))).Append('\n');
            sb.Append("| extend scMsg = ").Append(Pick(e => PerStep(e, st => Str(st.Exceptions.FirstOrDefault(x => x.Type == e.ExceptionType)?.Message
                ?? ReferenceSynthesizer.ScenarioMessage(e.ExceptionType!, st.Name))))).Append('\n');
            sb.Append("| extend scMethod = ").Append(Pick(e => PerStep(e, st => Str(st.Exceptions.FirstOrDefault(x => x.Type == e.ExceptionType)?.Method ?? "")))).Append('\n');
        }

        // Global index of the weighted exception of the step, -1 when the step declares none.
        var xi = new StringBuilder("case(");
        var offset = 0;
        foreach (var st in steps)
        {
            if (st.Exceptions.Count > 0)
            {
                xi.Append($"s == {st.Index}, ")
                  .Append(CaseByThreshold("u6", st.ExceptionCumulative, offset))
                  .Append(", ");
            }
            offset += st.Exceptions.Count;
        }
        xi.Append("-1)");
        sb.Append("| extend u6 = U(rk, 6)\n");
        sb.Append("| extend xi = ").Append(exceptions.Count == 0 ? "-1" : xi.ToString()).Append('\n');
        sb.Append("| extend sname = tostring(sName[s]), surl = tostring(sUrl[s]), ent = tostring(ents[e])\n");
        sb.Append("| extend exType = case(own and scType != \"\", scType, own and xi >= 0, tostring(xType[xi]), own, \"System.Exception\", mask != 0, ")
          .Append(Str(ReferenceSynthesizer.DependencyFailureException)).Append(", \"\")\n");
        sb.Append("| extend exMessage = case(own and scType != \"\", scMsg, own and xi >= 0, tostring(xMsg[xi]), own, strcat(\"Unhandled error while handling \", sname), ")
          .Append("mask != 0, strcat(\"Dependency call to \", tostring(dTarget[fd]), \" failed\"), \"\")\n");
        sb.Append("| extend exMethod = case(own and scType != \"\", scMethod, own and xi >= 0, tostring(xMethod[xi]), \"\")\n");
        sb.Append("| extend resultCode = case(success, \"200\", own and exType contains_cs \"Timeout\", \"504\", own, \"500\", \"502\")\n");
        sb.Append("| extend host = ").Append(app.EntityHosts is null ? Str(app.App.Host!) : "tostring(hosts[e])").Append('\n');
        sb.Append("| extend fullUrl = strcat(\"https://\", host, surl)\n");
        sb.Append("| extend opId = strcat(hex8(mix(rk + 1)), hex8(binary_xor(m, ").Append(app.OperationXorMinute.ToString(c))
          .Append(")), hex8(binary_xor(k, ").Append(app.OperationXorIndex.ToString(c)).Append(")), ")
          .Append(Str(DeterministicRandom.Hex8(app.Seed))).Append(")\n");
        sb.Append("| extend reqId = strcat(hex8(mix(rk + 2)), hex8(mix(rk + 3)))\n");
        sb.Append("| extend sessionId = strcat(hex8(sesH), hex8(binary_xor(sesAnchor, ").Append(app.SessionXorMinute.ToString(c))
          .Append("))), userId = strcat(\"u\", tostring(sesH % ").Append(ReferenceSynthesizer.SessionUsers.ToString(c)).Append("))\n");
        sb.Append("| extend country = tostring(countries[toint(U(rk, 10) * ").Append(ReferenceSynthesizer.Countries.Count.ToString(c)).Append(")])\n");
        sb.Append("| extend instance = strcat(").Append(Str(app.RoleName + "-")).Append(", tostring(mix(rk + 8) % 3))\n");
        sb.Append("| extend dims = bag_pack(").Append(Str(app.EntityDimension)).Append(", ent)");
        return sb.ToString();
    }

    private static string Projection(AppPlan app, IReadOnlyList<AppliedEffect> effects, string table)
    {
        var role = Str(app.RoleName);
        var appId = Str(app.AppId);
        var appName = Str(app.Database);
        var common = $"session_Id = sessionId, user_Id = userId";
        switch (table)
        {
            case "requests":
                return "| project timestamp = ts, id = reqId, name = sname, url = fullUrl, resultCode, duration, success, " +
                       $"performanceBucket = {PerformanceBucket("duration")}, itemType = \"request\", customDimensions = dims, customMeasurements = dynamic(null), " +
                       $"operation_Name = sname, operation_Id = opId, operation_ParentId = opId, {common}, user_AuthenticatedId = \"\", {Client}, " +
                       $"cloud_RoleName = {role}, cloud_RoleInstance = instance, appId = {appId}, appName = {appName}, iKey = {Str(app.IKey)}, " +
                       "sdkVersion = \"dotnetc:2.22.0-demo\", itemId = strcat(opId, \"-r\"), itemCount = toint(1), source = \"\"";

            case "dependencies":
                var dependencyLatency = Product(
                    effects.Where(e => e.Kind == EffectKinds.LatencyMultiplier && e.DependencyIndexes is not null),
                    e => Num(e.Factor), e => $" and di in ({Join(e.DependencyIndexes!)})");
                return "| where nd > 0\n" +
                       "| mv-expand j = range(0, nd - 1, 1) to typeof(long)\n" +
                       "| extend di = dOff + j\n" +
                       $"| extend dz = sqrt(-2.0 * log(U(rk, {ReferenceSynthesizer.ChannelDependency} + 10 * j + 1))) * cos(2.0 * pi() * U(rk, {ReferenceSynthesizer.ChannelDependency} + 10 * j + 2))\n" +
                       "| extend dSuccess = binary_and(mask, binary_shift_left(1, j)) == 0\n" +
                       $"| extend dDuration = exp(todouble(dMu[di]) + todouble(dSg[di]) * dz) * {dependencyLatency}\n" +
                       "| project timestamp = ts + 1ms * j, id = strcat(hex8(mix(rk + 200 + j)), hex8(mix(rk + 300 + j))), target = tostring(dTarget[di]), " +
                       "type = tostring(dType[di]), name = tostring(dName[di]), data = tostring(dName[di]), success = dSuccess, resultCode = iff(dSuccess, \"200\", \"500\"), " +
                       $"duration = dDuration, performanceBucket = {PerformanceBucket("dDuration")}, customDimensions = bag_merge(dims, bag_pack(\"Partner\", tostring(dPartner[di]))), " +
                       $"customMeasurements = dynamic(null), operation_Name = sname, operation_Id = opId, operation_ParentId = reqId, {common}, {Client}, " +
                       $"cloud_RoleName = {role}, cloud_RoleInstance = instance, appId = {appId}, appName = {appName}, itemId = strcat(opId, \"-d\", tostring(j)), itemCount = toint(1)";

            case "exceptions":
                var assembly = Str("Demo." + app.RoleName);
                return "| where not(success)\n" +
                       "| project timestamp = ts, problemId = strcat(exType, \" at \", iff(exMethod == \"\", sname, exMethod)), handledAt = \"UserCode\", " +
                       $"type = exType, message = exMessage, assembly = {assembly}, method = exMethod, outerType = exType, outerMessage = exMessage, outerAssembly = {assembly}, " +
                       $"outerMethod = exMethod, innermostType = exType, innermostMessage = exMessage, innermostAssembly = {assembly}, innermostMethod = exMethod, " +
                       "severityLevel = toint(3), details = pack_array(bag_pack(\"type\", exType, \"message\", exMessage)), customDimensions = dims, customMeasurements = dynamic(null), " +
                       $"operation_Name = sname, operation_Id = opId, operation_ParentId = reqId, {common}, {Client}, cloud_RoleName = {role}, cloud_RoleInstance = instance, " +
                       $"appId = {appId}, appName = {appName}, itemId = strcat(opId, \"-x\"), itemCount = toint(1)";

            case "traces":
                return $"| where not(success) or U(rk, {ReferenceSynthesizer.ChannelTrace}) < {Num(CompanyPlan.SuccessTraceRate)}\n" +
                       "| project timestamp = ts, message = iff(success, strcat(\"Handled \", sname, \" in \", tostring(tolong(duration)), \" ms\"), " +
                       "strcat(sname, \" failed: \", exType, \": \", exMessage)), severityLevel = toint(iff(success, 1, 3)), customDimensions = dims, customMeasurements = dynamic(null), " +
                       $"operation_Name = sname, operation_Id = opId, operation_ParentId = reqId, {common}, {Client}, cloud_RoleName = {role}, cloud_RoleInstance = instance, " +
                       $"appId = {appId}, appName = {appName}, itemId = strcat(opId, \"-t\"), itemCount = toint(1)";

            case "pageViews":
                return $"| where success and tobool(sPv[s]) and U(rk, {ReferenceSynthesizer.ChannelPageView}) < {Num(CompanyPlan.PageViewRate)}\n" +
                       "| extend pvDuration = duration * 1.6 + 300.0\n" +
                       $"| project timestamp = ts, id = strcat(hex8(mix(rk + 9)), hex8(mix(rk + 10))), name = surl, url = fullUrl, duration = pvDuration, " +
                       $"performanceBucket = {PerformanceBucket("pvDuration")}, customDimensions = dims, customMeasurements = dynamic(null), operation_Name = sname, " +
                       $"operation_Id = opId, {common}, {Client}, cloud_RoleName = {role}, appId = {appId}, appName = {appName}, itemId = strcat(opId, \"-p\"), itemCount = toint(1)";

            case "customEvents":
                var measured = app.Steps.Where(st => st.BusinessEvent is not null && st.Measurements.Count > 0).ToList();
                var measurements = measured.Count == 0
                    ? "dynamic(null)"
                    : "case(" + string.Join(", ", measured.Select(st => $"s == {st.Index}, bag_pack(" + string.Join(", ", st.Measurements.Select((pm, i) =>
                        $"{Str(pm.Name)}, {Num(pm.Min)} + U(rk, {ReferenceSynthesizer.ChannelMeasurement + i}) * {Num(pm.Max - pm.Min)}")) + ")")) + ", dynamic(null))";
                return "| where tostring(sBe[s]) != \"\" and (success or not(tobool(sBeS[s])))\n" +
                       $"| project timestamp = ts, name = tostring(sBe[s]), customDimensions = dims, customMeasurements = {measurements}, operation_Name = sname, " +
                       $"operation_Id = opId, {common}, {Client}, cloud_RoleName = {role}, appId = {appId}, appName = {appName}, itemId = strcat(opId, \"-e\"), itemCount = toint(1)";

            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, "Not an App Insights table of the demo schema.");
        }
    }

    private static string PerformanceBucket(string column) =>
        $"case({column} < 250.0, \"<250ms\", {column} < 500.0, \"250ms-500ms\", {column} < 1000.0, \"500ms-1sec\", {column} < 3000.0, \"1sec-3sec\", " +
        $"{column} < 7000.0, \"3sec-7sec\", {column} < 15000.0, \"7sec-15sec\", {column} < 30000.0, \"15sec-30sec\", {column} < 60000.0, \"30sec-1min\", " +
        $"{column} < 120000.0, \"1min-2min\", \">=2min\")";

    private static string EntityRankExpression(AppPlan app)
    {
        var n = app.EntityIds.Count;
        var c = CultureInfo.InvariantCulture;
        string x;
        if (app.Zipf == 0) x = $"(1.0 + u2 * {Num(n)})";
        else if (app.Zipf == 1) x = $"exp(u2 * log({Num(n + 1.0)}))";
        else x = $"pow(1.0 + u2 * {Num(Math.Pow(n + 1.0, 1 - app.Zipf) - 1)}, {Num(1 / (1 - app.Zipf))})";
        return $"toint(min_of({(n - 1).ToString(c)}, max_of(0, tolong({x}) - 1)))";
    }

    /// <summary><c>case(u &lt; c0, 0, u &lt; c1, 1, ..., last)</c>; the last bucket also takes u at or above its threshold.</summary>
    private static string CaseByThreshold(string u, IReadOnlyList<double> cumulative, int offset = 0)
    {
        var c = CultureInfo.InvariantCulture;
        if (cumulative.Count == 1) return offset.ToString(c);
        var parts = new List<string>();
        for (var i = 0; i < cumulative.Count - 1; i++) parts.Add($"{u} < {Num(cumulative[i])}, {(offset + i).ToString(c)}");
        return "case(" + string.Join(", ", parts) + ", " + (offset + cumulative.Count - 1).ToString(c) + ")";
    }

    private static string Product(IEnumerable<AppliedEffect> effects, Func<AppliedEffect, string> value, Func<AppliedEffect, string> extraPredicate)
    {
        var terms = effects.Select(e => $"iff({Predicate(e)}{extraPredicate(e)}, {value(e)}, 1.0)").ToList();
        return terms.Count == 0 ? "1.0" : "(" + string.Join(" * ", terms) + ")";
    }

    private static string Predicate(AppliedEffect e)
    {
        var p = $"(mts >= {Date(e.Start)} and mts < {Date(e.End)}";
        if (e.Steps is not null) p += $" and s in ({Join(e.Steps)})";
        if (e.Entities is not null) p += $" and e in ({Join(e.Entities)})";
        return p + ")";
    }

    private static void Let(StringBuilder sb, string name, IEnumerable<string> values) =>
        sb.Append("let ").Append(name).Append(" = dynamic([").Append(string.Join(", ", values)).Append("]);\n");

    private static string Join(IEnumerable<int> values) => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    public static string Date(DateTimeOffset value) =>
        "datetime(" + value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) + ")";

    /// <summary>A real literal with full round-trip precision; valid both in expressions and inside <c>dynamic([...])</c>.</summary>
    public static string Num(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>A KQL string literal: double quotes with backslash escapes.</summary>
    public static string Str(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
