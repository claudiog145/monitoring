-- Migration: Create AppInsightsTokens table
-- For: Option 1 - Token Externo (External Token Storage)
-- Date: 2024-10-30

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppInsightsTokens')
BEGIN
    CREATE TABLE [dbo].[AppInsightsTokens]
    (
        -- Primary key
        [Id] INT PRIMARY KEY IDENTITY(1,1),
        
        -- Tenant identification
        [TenantKey] NVARCHAR(50) NOT NULL,
        
        -- Audit trail
        [Email] NVARCHAR(255) NOT NULL,
        
        -- Token storage (encrypted)
        [EncryptedToken] NVARCHAR(MAX) NOT NULL,
        
        -- Token metadata
        [ExpiresAt] DATETIME2 NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        [LastUsedAt] DATETIME2 NULL,
        
        -- Status
        [IsActive] BIT NOT NULL DEFAULT 1,
        
        -- Audit info
        [Notes] NVARCHAR(500) NULL,
        [UsageCount] INT NOT NULL DEFAULT 0,
        
        -- Constraints
        CONSTRAINT UK_AppInsightsTokens_TenantKey UNIQUE (TenantKey)
    );

    -- Create index for faster lookups by tenant
    CREATE NONCLUSTERED INDEX IX_AppInsightsTokens_TenantKey
    ON [dbo].[AppInsightsTokens] ([TenantKey])
    WHERE [IsActive] = 1;

    -- Create index for audit queries
    CREATE NONCLUSTERED INDEX IX_AppInsightsTokens_CreatedAt
    ON [dbo].[AppInsightsTokens] ([CreatedAt] DESC);

    PRINT 'Table [AppInsightsTokens] created successfully';
END
ELSE
BEGIN
    PRINT 'Table [AppInsightsTokens] already exists';
END

-- Add columns if they don't exist (for existing databases)
IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'TenantKey'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [TenantKey] NVARCHAR(50) NOT NULL DEFAULT '';
    ALTER TABLE [dbo].[AppInsightsTokens] ADD CONSTRAINT UK_AppInsightsTokens_TenantKey UNIQUE (TenantKey);
    PRINT 'Column [TenantKey] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'Email'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [Email] NVARCHAR(255) NOT NULL DEFAULT '';
    PRINT 'Column [Email] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'EncryptedToken'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [EncryptedToken] NVARCHAR(MAX) NOT NULL DEFAULT '';
    PRINT 'Column [EncryptedToken] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'ExpiresAt'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [ExpiresAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE();
    PRINT 'Column [ExpiresAt] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'CreatedAt'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE();
    PRINT 'Column [CreatedAt] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'LastUsedAt'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [LastUsedAt] DATETIME2 NULL;
    PRINT 'Column [LastUsedAt] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'IsActive'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [IsActive] BIT NOT NULL DEFAULT 1;
    PRINT 'Column [IsActive] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'Notes'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [Notes] NVARCHAR(500) NULL;
    PRINT 'Column [Notes] added';
END

IF NOT EXISTS (
    SELECT * FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.AppInsightsTokens') 
    AND name = 'UsageCount'
)
BEGIN
    ALTER TABLE [dbo].[AppInsightsTokens] ADD [UsageCount] INT NOT NULL DEFAULT 0;
    PRINT 'Column [UsageCount] added';
END
