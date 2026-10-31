using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// SQL Server implementation of IAppInsightsTokenRepository
    /// </summary>
    public class SqlAppInsightsTokenRepository : IAppInsightsTokenRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlAppInsightsTokenRepository> _logger;

        public SqlAppInsightsTokenRepository(
            string connectionString,
            ILogger<SqlAppInsightsTokenRepository> logger)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SaveAsync(AppInsightsToken token)
        {
            if (token == null)
                throw new ArgumentNullException(nameof(token));

            const string query = @"
                INSERT INTO [dbo].[AppInsightsTokens] 
                    (TenantKey, Email, EncryptedToken, ExpiresAt, CreatedAt, IsActive, Notes, UsageCount, LastUsedAt)
                VALUES 
                    (@TenantKey, @Email, @EncryptedToken, @ExpiresAt, @CreatedAt, @IsActive, @Notes, @UsageCount, @LastUsedAt)
            ";

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 30;
                        
                        command.Parameters.AddWithValue("@TenantKey", token.TenantKey);
                        command.Parameters.AddWithValue("@Email", token.Email);
                        command.Parameters.AddWithValue("@EncryptedToken", token.EncryptedToken);
                        command.Parameters.AddWithValue("@ExpiresAt", token.ExpiresAt);
                        command.Parameters.AddWithValue("@CreatedAt", token.CreatedAt);
                        command.Parameters.AddWithValue("@IsActive", token.IsActive);
                        command.Parameters.AddWithValue("@Notes", token.Notes ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UsageCount", token.UsageCount);
                        command.Parameters.AddWithValue("@LastUsedAt", token.LastUsedAt ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger.LogInformation($"Token saved for tenant {token.TenantKey}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving token: {ex}");
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
                    LastUsedAt = @LastUsedAt
                WHERE TenantKey = @TenantKey
            ";

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 30;
                        
                        command.Parameters.AddWithValue("@TenantKey", token.TenantKey);
                        command.Parameters.AddWithValue("@Email", token.Email);
                        command.Parameters.AddWithValue("@EncryptedToken", token.EncryptedToken);
                        command.Parameters.AddWithValue("@ExpiresAt", token.ExpiresAt);
                        command.Parameters.AddWithValue("@IsActive", token.IsActive);
                        command.Parameters.AddWithValue("@Notes", token.Notes ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@UsageCount", token.UsageCount);
                        command.Parameters.AddWithValue("@LastUsedAt", token.LastUsedAt ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger.LogInformation($"Token updated for tenant {token.TenantKey}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating token: {ex}");
                throw;
            }
        }

        public async Task<AppInsightsToken> GetByTenantKeyAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                return null;

            const string query = @"
                SELECT TOP 1
                    Id, TenantKey, Email, EncryptedToken, ExpiresAt, CreatedAt, 
                    IsActive, Notes, UsageCount, LastUsedAt
                FROM [dbo].[AppInsightsTokens]
                WHERE TenantKey = @TenantKey
                ORDER BY CreatedAt DESC
            ";

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 30;
                        command.Parameters.AddWithValue("@TenantKey", tenantKey);

                        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
                        {
                            if (await reader.ReadAsync())
                            {
                                return new AppInsightsToken
                                {
                                    Id = reader.GetInt32(0),
                                    TenantKey = reader.GetString(1),
                                    Email = reader.GetString(2),
                                    EncryptedToken = reader.GetString(3),
                                    ExpiresAt = reader.GetDateTime(4),
                                    CreatedAt = reader.GetDateTime(5),
                                    IsActive = reader.GetBoolean(6),
                                    Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
                                    UsageCount = reader.GetInt32(8),
                                    LastUsedAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9)
                                };
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting token for tenant {tenantKey}: {ex}");
                throw;
            }
        }

        public async Task<List<AppInsightsToken>> GetAllAsync()
        {
            const string query = @"
                SELECT 
                    Id, TenantKey, Email, EncryptedToken, ExpiresAt, CreatedAt, 
                    IsActive, Notes, UsageCount, LastUsedAt
                FROM [dbo].[AppInsightsTokens]
                ORDER BY CreatedAt DESC
            ";

            var tokens = new List<AppInsightsToken>();

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 30;

                        using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
                        {
                            while (await reader.ReadAsync())
                            {
                                tokens.Add(new AppInsightsToken
                                {
                                    Id = reader.GetInt32(0),
                                    TenantKey = reader.GetString(1),
                                    Email = reader.GetString(2),
                                    EncryptedToken = reader.GetString(3),
                                    ExpiresAt = reader.GetDateTime(4),
                                    CreatedAt = reader.GetDateTime(5),
                                    IsActive = reader.GetBoolean(6),
                                    Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
                                    UsageCount = reader.GetInt32(8),
                                    LastUsedAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting all tokens: {ex}");
                throw;
            }

            return tokens;
        }

        public async Task DeleteAsync(int id)
        {
            const string query = @"DELETE FROM [dbo].[AppInsightsTokens] WHERE Id = @Id";

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 30;
                        command.Parameters.AddWithValue("@Id", id);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger.LogInformation($"Token deleted: {id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deleting token: {ex}");
                throw;
            }
        }
    }
}
