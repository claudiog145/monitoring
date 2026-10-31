using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Same idea as <see cref="MonitoringSchemaGate"/>: one catalog lookup, not a migration.
/// Connection and authentication failures propagate. Those are not "the script has not been
/// applied," and swallowing them would hide a database that journey sync can no longer reach.
/// The procedure check is repeated on each timer run so applying the script takes effect on
/// the next run without a process restart.
/// </summary>
public sealed class SqlIncidentSchemaGate(IOptions<JourneyStoreOptions> storeOptions) : IIncidentSchemaGate
{
    public bool IsProvisioned()
    {
        var connectionString = storeOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT OBJECT_ID(N'incident.usp_IngestExceptions', N'P')";
        return command.ExecuteScalar() is not null and not DBNull;
    }
}
