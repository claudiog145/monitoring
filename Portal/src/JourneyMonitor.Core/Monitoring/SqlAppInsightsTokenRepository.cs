using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// SQL Server implementation of IAppInsightsTokenRepository (dbo.AppInsightsTokens). Written against
    /// ADO.NET's DbConnection so Core does not depend on a data provider: the host passes a factory
    /// that opens its SqlConnection.
    /// </summary>
    public class SqlAppInsightsTokenRepository : IAppInsightsTokenRepository
    {
        private const string SelectColumns = @"
                    Id, TenantKey, Email, EncryptedToken, ExpiresAt, CreatedAt,
                    IsActive, Notes, UsageCount, LastUsedAt,
                    RefreshMode, AadTenantId, ClientId, EncryptedClientSecret,
                    LastRefreshAt, LastRefreshError, RefreshCount";

        private readonly Func<DbConnection> _connectionFactory;
        private readonly ILogger<SqlAppInsightsTokenRepository> _logger;

        public SqlAppInsightsTokenRepository(
            Func<DbConnection> connectionFactory,
            ILogger<SqlAppInsightsTokenRepository> logger)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SaveAsync(AppInsightsToken token)
        {
            if (token == null)
                throw new ArgumentNullException(nameof(token));

            const string query = @"
                INSERT INTO [dbo].[AppInsightsTokens] 
                    (TenantKey, Email, EncryptedToken, ExpiresAt, CreatedAt, IsActive, Notes, UsageCount, LastUsedAt,
                     RefreshMode, AadTenantId, ClientId, EncryptedClientSecret, LastRefreshAt, LastRefreshError, RefreshCount)
                VALUES 
                    (@TenantKey, @Email, @EncryptedToken, @ExpiresAt, @CreatedAt, @IsActive, @Notes, @UsageCount, @LastUsedAt,
                     @RefreshMode, @AadTenantId, @ClientId, @EncryptedClientSecret, @LastRefreshAt, @LastRefreshError, @RefreshCount)
            ";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameters(command, token);
                AddParameter(command, "@CreatedAt", token.CreatedAt);
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("Token saved for tenant {TenantKey}", token.TenantKey);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error saving token for tenant {TenantKey}: {Error}", token.TenantKey, ex.Message);
                throw;
            }
        }

        public async Task UpdateAsync(AppInsightsToken token)
        {
            if (token == null)
                throw new ArgumentNullException(nameof(token));

            const string query = @"
                UPDATE [dbo].[AppInsightsTokens]
                SET 
                    Email = @Email,
                    EncryptedToken = @EncryptedToken,
                    ExpiresAt = @ExpiresAt,
                    IsActive = @IsActive,
                    Notes = @Notes,
                    UsageCount = @UsageCount,
                    LastUsedAt = @LastUsedAt,
                    RefreshMode = @RefreshMode,
                    AadTenantId = @AadTenantId,
                    ClientId = @ClientId,
                    EncryptedClientSecret = @EncryptedClientSecret,
                    LastRefreshAt = @LastRefreshAt,
                    LastRefreshError = @LastRefreshError,
                    RefreshCount = @RefreshCount
                WHERE TenantKey = @TenantKey
            ";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameters(command, token);
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("Token updated for tenant {TenantKey}", token.TenantKey);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error updating token for tenant {TenantKey}: {Error}", token.TenantKey, ex.Message);
                throw;
            }
        }

        public async Task<AppInsightsToken> GetByTenantKeyAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                return null!;

            var query = $@"
                SELECT TOP 1 {SelectColumns}
                FROM [dbo].[AppInsightsTokens]
                WHERE TenantKey = @TenantKey
                ORDER BY CreatedAt DESC
            ";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameter(command, "@TenantKey", tenantKey);

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                return await reader.ReadAsync() ? Read(reader) : null!;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting token for tenant {TenantKey}: {Error}", tenantKey, ex.Message);
                throw;
            }
        }

        public async Task<List<AppInsightsToken>> GetAllAsync()
        {
            var query = $@"
                SELECT {SelectColumns}
                FROM [dbo].[AppInsightsTokens]
                ORDER BY CreatedAt DESC
            ";

            var tokens = new List<AppInsightsToken>();

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                while (await reader.ReadAsync())
                {
                    tokens.Add(Read(reader));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting all tokens: {Error}", ex.Message);
                throw;
            }

            return tokens;
        }

        public async Task DeleteAsync(int id)
        {
            const string query = @"DELETE FROM [dbo].[AppInsightsTokens] WHERE Id = @Id";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameter(command, "@Id", id);
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("Token deleted: {Id}", id);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error deleting token {Id}: {Error}", id, ex.Message);
                throw;
            }
        }

        public async Task<bool> TryUpdateRefreshedTokenAsync(AppInsightsToken refreshed, AppInsightsToken readState)
        {
            if (refreshed == null) throw new ArgumentNullException(nameof(refreshed));
            if (readState == null) throw new ArgumentNullException(nameof(readState));

            // Optimistic check: every user-editable column must still hold what the refresh read.
            const string query = @"
                UPDATE [dbo].[AppInsightsTokens]
                SET 
                    EncryptedToken = @EncryptedToken,
                    ExpiresAt = @ExpiresAt,
                    LastRefreshAt = @LastRefreshAt,
                    LastRefreshError = NULL,
                    RefreshCount = @RefreshCount
                WHERE TenantKey = @TenantKey
                  AND EncryptedToken = @ReadEncryptedToken
                  AND RefreshMode = @ReadRefreshMode
                  AND ISNULL(AadTenantId, N'') = @ReadAadTenantId
                  AND ISNULL(ClientId, N'') = @ReadClientId
                  AND ISNULL(EncryptedClientSecret, N'') = @ReadEncryptedClientSecret
                  AND IsActive = 1
            ";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameter(command, "@TenantKey", refreshed.TenantKey);
                AddParameter(command, "@EncryptedToken", refreshed.EncryptedToken ?? string.Empty);
                AddParameter(command, "@ExpiresAt", refreshed.ExpiresAt);
                AddParameter(command, "@LastRefreshAt", refreshed.LastRefreshAt);
                AddParameter(command, "@RefreshCount", refreshed.RefreshCount);
                AddParameter(command, "@ReadEncryptedToken", readState.EncryptedToken ?? string.Empty);
                AddParameter(command, "@ReadRefreshMode", AppInsightsTokenRefreshModes.Normalize(readState.RefreshMode) ?? AppInsightsTokenRefreshModes.Manual);
                AddParameter(command, "@ReadAadTenantId", readState.AadTenantId ?? string.Empty);
                AddParameter(command, "@ReadClientId", readState.ClientId ?? string.Empty);
                AddParameter(command, "@ReadEncryptedClientSecret", readState.EncryptedClientSecret ?? string.Empty);
                return await command.ExecuteNonQueryAsync() > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error saving refreshed token for tenant {TenantKey}: {Error}", refreshed.TenantKey, ex.Message);
                throw;
            }
        }

        public async Task UpdateRefreshErrorAsync(string tenantKey, string? error)
        {
            const string query = @"UPDATE [dbo].[AppInsightsTokens] SET LastRefreshError = @LastRefreshError WHERE TenantKey = @TenantKey";

            try
            {
                await using var connection = _connectionFactory();
                await connection.OpenAsync();
                await using var command = CreateCommand(connection, query);
                AddParameter(command, "@TenantKey", tenantKey);
                AddParameter(command, "@LastRefreshError", Truncate(error, 500));
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error recording refresh error for tenant {TenantKey}: {Error}", tenantKey, ex.Message);
                throw;
            }
        }

        private static DbCommand CreateCommand(DbConnection connection, string query)
        {
            var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = 30;
            return command;
        }

        private static void AddParameter(DbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static void AddParameters(DbCommand command, AppInsightsToken token)
        {
            AddParameter(command, "@TenantKey", token.TenantKey);
            AddParameter(command, "@Email", token.Email);
            AddParameter(command, "@EncryptedToken", token.EncryptedToken ?? string.Empty);
            AddParameter(command, "@ExpiresAt", token.ExpiresAt);
            AddParameter(command, "@IsActive", token.IsActive);
            AddParameter(command, "@Notes", token.Notes);
            AddParameter(command, "@UsageCount", token.UsageCount);
            AddParameter(command, "@LastUsedAt", token.LastUsedAt);
            AddParameter(command, "@RefreshMode", AppInsightsTokenRefreshModes.Normalize(token.RefreshMode) ?? AppInsightsTokenRefreshModes.Manual);
            AddParameter(command, "@AadTenantId", token.AadTenantId);
            AddParameter(command, "@ClientId", token.ClientId);
            AddParameter(command, "@EncryptedClientSecret", token.EncryptedClientSecret);
            AddParameter(command, "@LastRefreshAt", token.LastRefreshAt);
            AddParameter(command, "@LastRefreshError", Truncate(token.LastRefreshError, 500));
            AddParameter(command, "@RefreshCount", token.RefreshCount);
        }

        private static string? Truncate(string? value, int max) =>
            value is null || value.Length <= max ? value : value[..max];

        // Columns are read in SELECT order because the reader uses SequentialAccess.
        private static AppInsightsToken Read(DbDataReader reader) => new AppInsightsToken
        {
            Id = reader.GetInt32(0),
            TenantKey = reader.GetString(1),
            Email = reader.GetString(2),
            EncryptedToken = reader.GetString(3),
            ExpiresAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            CreatedAt = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
            IsActive = reader.GetBoolean(6),
            Notes = reader.IsDBNull(7) ? null! : reader.GetString(7),
            UsageCount = reader.GetInt32(8),
            LastUsedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            RefreshMode = reader.IsDBNull(10) ? AppInsightsTokenRefreshModes.Manual : reader.GetString(10),
            AadTenantId = reader.IsDBNull(11) ? null : reader.GetString(11),
            ClientId = reader.IsDBNull(12) ? null : reader.GetString(12),
            EncryptedClientSecret = reader.IsDBNull(13) ? null : reader.GetString(13),
            LastRefreshAt = reader.IsDBNull(14) ? null : DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc),
            LastRefreshError = reader.IsDBNull(15) ? null : reader.GetString(15),
            RefreshCount = reader.GetInt32(16),
        };
    }
}
