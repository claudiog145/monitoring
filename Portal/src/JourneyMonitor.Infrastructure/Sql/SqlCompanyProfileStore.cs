using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed company profiles (monitoring.CompanyProfile). Same Dapper shape as the other
/// monitoring settings stores, with one deliberate difference: the profile is a JSON document,
/// not a set of columns, so nothing here tries to let Dapper materialise
/// <see cref="CompanyProfile"/> directly. Dapper needs a constructor matching every selected
/// column to bind a positional record, and the selected columns are the row metadata
/// (Version/DisplayName/Enabled/Notes/UpdatedAt/UpdatedBy) plus one blob - a shape the domain
/// record will never have. Everything goes through <see cref="ProfileRow"/> and the document is
/// deserialised by hand.
///
/// <see cref="JsonSerializerDefaults.Web"/> on purpose: the document is written and read back by
/// property name, and PascalCase persistence would hand the UI a profile whose every field is
/// null without a single error.
/// </summary>
public sealed class SqlCompanyProfileStore(IOptions<JourneyStoreOptions> storeOptions) : ICompanyProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);

        await using var connection = OpenConnection();
        // The proc filters Enabled = 1 - a disabled profile must not classify traffic.
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            "monitoring.usp_GetCompanyProfile", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ProfileRow>(new CommandDefinition(
            "monitoring.usp_GetCompanyProfiles", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", profile.ProfileKey, DbType.String, size: 128);
        parameters.Add("ProfileJson", JsonSerializer.Serialize(profile, JsonOptions), DbType.String, size: -1);
        parameters.Add("DisplayName", profile.DisplayName, DbType.String, size: 256);
        parameters.Add("Enabled", profile.Enabled, DbType.Boolean);
        parameters.Add("Notes", profile.Notes, DbType.String, size: 1000);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            "monitoring.usp_UpsertCompanyProfile", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_DeleteCompanyProfile", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "monitoring.usp_GetCompanyProfileAudit", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record ProfileRow(
        string ProfileKey, string ProfileJson, int Version, string? DisplayName,
        bool Enabled, string? Notes, DateTimeOffset UpdatedAt, string UpdatedBy)
    {
        public CompanyProfile ToDomain()
        {
            CompanyProfile? document;
            try
            {
                document = JsonSerializer.Deserialize<CompanyProfile>(ProfileJson, JsonOptions);
            }
            catch (JsonException ex)
            {
                // Not swallowed into "no profile": a profile we cannot read is OUR outage, and
                // CompanyResolution keeps Unavailable and Unresolved apart precisely so that
                // failure sends the right person to look. Dropping the row from a list would
                // instead look exactly like a company nobody has onboarded yet.
                throw new InvalidOperationException(
                    $"monitoring.CompanyProfile row '{ProfileKey}' does not contain a readable CompanyProfile document.", ex);
            }

            if (document is null)
            {
                throw new InvalidOperationException(
                    $"monitoring.CompanyProfile row '{ProfileKey}' contains a null document.");
            }

            // Identity and lifecycle come from the columns, content comes from the document. The
            // columns are what the upsert, the audit and usp_GetCompanyProfile's Enabled filter
            // are keyed on, so a row edited directly in SSMS must not be able to hand back a
            // document that disagrees with its own history. DisplayName/Notes are content and
            // stay as written - their columns are a denormalised copy for the list view.
            return document with
            {
                ProfileKey = ProfileKey,
                Version = Version,
                Enabled = Enabled,
                UpdatedAt = UpdatedAt,
                UpdatedBy = UpdatedBy
            };
        }
    }

    private sealed record AuditRow(
        long AuditId, string ProfileKey, int Version, string ProfileJson,
        bool Removed, DateTimeOffset ChangedAt, string ChangedBy)
    {
        public CompanyProfileAuditEntry ToDomain() =>
            new(AuditId, ProfileKey, Version, ProfileJson, Removed, ChangedAt, ChangedBy);
    }
}
