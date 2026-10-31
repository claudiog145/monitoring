using Microsoft.Data.SqlClient;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Startup-time check for AddJourneyMonitorMonitoring: a JourneyStore connection string being
/// configured is not proof the monitoring.* schema/procs from Sql/monitoring/*.sql have actually
/// been applied against that database - locally, docker-compose's sql-init service guarantees it;
/// a first QA/production deployment against an existing JourneyStore does not run those scripts
/// automatically (see publish-func-qa.ps1 - it only builds/updates the Function image). Selecting
/// the SQL repositories anyway would fail every timer run, /api/monkeys call and Settings write
/// with a missing-table/procedure error instead of the documented "not configured -> use
/// InMemory*" fallback. Checking monitoring.Finding and monitoring.NotificationLog is a cheap
/// proxy for "the whole monitoring schema batch ran" - all monitoring/*.sql scripts are applied
/// together by init-sql.sh, never independently. NotificationLog is checked separately because it
/// comes from a later migration (Sql/monitoring/CreateMonitoringNotificationLog.sql) added after
/// Finding - an environment upgraded before that script ran would otherwise pass this gate, select
/// SqlNotificationLogRepository, and fail every /api/monkeys call the moment it queries that table.
///
/// Deliberately does NOT catch connection/auth/timeout failures here: those are a different
/// problem than "schema not provisioned" and must not be silently downgraded to "use InMemory*
/// for the rest of this process's lifetime" (the DI choice below is made once at startup, not
/// re-evaluated later, so a transient outage during startup would otherwise mean Findings/reports
/// never persist to SQL again even after the database recovers). Let those exceptions propagate
/// and fail startup instead - the orchestrator restarting the process is the correct recovery
/// path for "the database is unreachable right now", not a silent, permanent fallback.
/// </summary>
internal static class MonitoringSchemaGate
{
    internal static bool IsProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.Finding', N'U'), OBJECT_ID(N'monitoring.NotificationLog', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1);
    }

    /// <summary>
    /// The Inspector's own tables (Sql/monitoring/CreateMonitoringInspector.sql) ship later than
    /// Finding/NotificationLog, so an environment upgraded before that script ran passes the gate
    /// above and would still fail every Inspector timer tick on a missing table. Checked
    /// separately for the same reason NotificationLog is.
    /// </summary>
    internal static bool IsInspectorProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText =
            "SELECT OBJECT_ID(N'monitoring.AgentWorkQueue', N'U'), OBJECT_ID(N'monitoring.ForensicCase', N'U'), OBJECT_ID(N'monitoring.AgentTurn', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2);
    }

    /// <summary>
    /// The pause switch table (Sql/monitoring/CreateMonitoringAgentPause.sql) ships later than
    /// Finding/NotificationLog, so an environment upgraded before that script ran would select
    /// SqlAgentPauseStore and fail on a missing table every tick. Checked separately for the same
    /// reason NotificationLog is.
    /// </summary>
    internal static bool IsAgentPauseProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.AgentPauseSetting', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    /// <summary>The ownership map ships later again - same late-migration reasoning as the two gates above.</summary>
    internal static bool IsServiceOwnerProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.ServiceOwner', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    /// <summary>
    /// The telemetry source catalog ships last - same late-migration reasoning again. Missing it is
    /// less severe than the gates above: KqlSourceResolver falls back to TelemetrySourceDefaults, so
    /// queries still resolve against the built-in resource ids. What is lost is the ability to point
    /// the platform at a different subscription from the portal, and the audit trail of who did.
    /// </summary>
    internal static bool IsTelemetrySourceProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.TelemetrySource', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    /// <summary>
    /// The editable-query table ships alongside the source catalog. Missing it degrades the same
    /// way: every key falls through to its embedded text, which is the text that shipped, so
    /// nothing breaks - what is lost is editing a query without a redeploy.
    /// </summary>
    internal static bool IsKqlQueryProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.KqlQuery', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    /// <summary>
    /// The RCA Agent's own table (Sql/monitoring/CreateMonitoringRca.sql) ships later than
    /// Finding/NotificationLog, same late-migration reasoning as the gates above. Without this
    /// check SqlRcaReportRepository would be selected on an environment that never ran that
    /// script, and every composed RCA would fail to persist on a missing table/procedure - while
    /// both the on-demand route and the auto-composer still hand back (or silently accept) a
    /// fully composed document, so the failure is invisible until someone looks for the report in
    /// the RCA Agent card and it is not there.
    /// </summary>
    internal static bool IsRcaProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.RcaReport', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }

    /// <summary>
    /// The company profile table (Sql/monitoring/CreateMonitoringCompanyProfile.sql) ships last of
    /// all - same late-migration reasoning as the gates above. Missing it degrades the same way
    /// the telemetry catalog does, one level worse: the profile falls back to whatever the
    /// composition root seeded into InMemoryCompanyProfileStore, so onboarding a company in the
    /// portal holds only until the process restarts, and the audit trail of who changed the
    /// mapping that classified every alert does not exist at all.
    /// </summary>
    internal static bool IsCompanyProfileProvisioned(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'monitoring.CompanyProfile', N'U')";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0);
    }
}
