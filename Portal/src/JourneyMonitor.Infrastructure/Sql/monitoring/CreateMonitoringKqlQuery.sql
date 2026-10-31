-- Editable KQL, versioned. Grain: one row per QueryKey in monitoring.KqlQuery, with
-- KqlQueryAudit as append-only history - same shape as monitoring.ServiceOwner(+Audit).
--
-- Why the audit carries the full KqlText and not just "the text changed": this is the one setting
-- in the schema whose VALUE is the thing worth auditing. monitoring.LlmClientSettingAudit records
-- only TokenChanged because the value is a secret; here the value is the query that produced (or
-- failed to produce) an alert, so a reader has to be able to see exactly what ran, and restoring a
-- previous version has to be a copy rather than a reconstruction.
--
-- Precedence at runtime: a row here wins over the file embedded in the assembly. A missing or
-- disabled row falls back to the embedded text, so a database that was never seeded behaves
-- exactly like the pre-refactor build - see DbKqlQueryTextProvider.
--
-- Queries here may only reference telemetry through {{SOURCE:alias}}; KqlSourceResolver rejects a
-- literal /subscriptions/... path, which keeps monitoring.TelemetrySource as the allow-list of
-- everything a stored query is able to read.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.KqlQuery', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KqlQuery
    (
        -- Stable identity used by the code, e.g. 'scout:latency', 'inspector:club-baseline',
        -- 'journey:hotels', 'dashboard:cruises:step_latency_p95'. No CHECK: a new query must be a
        -- row, not a schema migration.
        QueryKey     nvarchar(128)     NOT NULL
            CONSTRAINT PK_monitoring_KqlQuery PRIMARY KEY,
        KqlText      nvarchar(max)     NOT NULL,
        -- Incremented by the upsert. Lets the UI show "v7" and the audit identify what to restore.
        Version      int               NOT NULL
            CONSTRAINT DF_monitoring_KqlQuery_Version DEFAULT 1,
        Description  nvarchar(500)     NULL,
        -- Where the query came from: the workbook id, a PR, a session doc. Provenance used to live
        -- in a comment at the top of the file and was lost the moment the text became editable.
        SourceRef    nvarchar(500)     NULL,
        -- 1 when this key also exists as an embedded file, so the UI can offer "revert to built-in".
        IsBuiltIn    bit               NOT NULL
            CONSTRAINT DF_monitoring_KqlQuery_IsBuiltIn DEFAULT 0,
        Enabled      bit               NOT NULL
            CONSTRAINT DF_monitoring_KqlQuery_Enabled DEFAULT 1,
        UpdatedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_KqlQuery_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_KqlQuery_UpdatedBy DEFAULT N''
    );
END
GO

IF OBJECT_ID(N'monitoring.KqlQueryAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KqlQueryAudit
    (
        AuditId      bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_KqlQueryAudit PRIMARY KEY,
        QueryKey     nvarchar(128)     NOT NULL,
        Version      int               NOT NULL,
        -- The whole text, on purpose. See the header: this is what makes rollback a copy.
        KqlText      nvarchar(max)     NULL,
        Description  nvarchar(500)     NULL,
        SourceRef    nvarchar(500)     NULL,
        Enabled      bit               NULL,
        -- Set when the row was removed, so "this query went back to the built-in" is a recorded event.
        Removed      bit               NOT NULL
            CONSTRAINT DF_monitoring_KqlQueryAudit_Removed DEFAULT 0,
        ChangedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_KqlQueryAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_KqlQueryAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_KqlQueryAudit_Read
        ON monitoring.KqlQueryAudit (QueryKey, ChangedAt DESC);
END
GO

-- Text is excluded here: the list view renders 18 rows and the bodies run to hundreds of lines each.
CREATE OR ALTER PROCEDURE monitoring.usp_GetAllKqlQueries
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QueryKey, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy,
           -- CAST because DATALENGTH is bigint and the reader materializes an int.
           CAST(DATALENGTH(KqlText) / 2 AS int) AS TextLength
    FROM monitoring.KqlQuery
    ORDER BY QueryKey;
END
GO

-- Every enabled query with its text, in one round trip: DbKqlQueryTextProvider refreshes the whole
-- snapshot rather than reading per key, because it is consulted from timers on a hot path.
CREATE OR ALTER PROCEDURE monitoring.usp_GetEnabledKqlQueryTexts
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QueryKey, KqlText, Version
    FROM monitoring.KqlQuery
    WHERE Enabled = 1;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKqlQuery
    @QueryKey nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT QueryKey, KqlText, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy
    FROM monitoring.KqlQuery
    WHERE QueryKey = @QueryKey;
END
GO

-- MERGE WITH (HOLDLOCK) rather than IF NOT EXISTS + UPDATE: two concurrent writers on the same
-- QueryKey would otherwise race between the check and the insert, and the version would skip.
CREATE OR ALTER PROCEDURE monitoring.usp_UpsertKqlQuery
    @QueryKey nvarchar(128), @KqlText nvarchar(max), @Description nvarchar(500),
    @SourceRef nvarchar(500), @IsBuiltIn bit, @Enabled bit, @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NextVersion int =
        ISNULL((SELECT Version + 1 FROM monitoring.KqlQuery WHERE QueryKey = @QueryKey), 1);

    MERGE monitoring.KqlQuery WITH (HOLDLOCK) AS target
    USING (SELECT @QueryKey AS QueryKey) AS source
        ON target.QueryKey = source.QueryKey
    WHEN MATCHED THEN
        UPDATE SET KqlText = @KqlText, Version = @NextVersion, Description = @Description,
                   SourceRef = @SourceRef, IsBuiltIn = @IsBuiltIn, Enabled = @Enabled,
                   UpdatedAt = SYSDATETIMEOFFSET(), UpdatedBy = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (QueryKey, KqlText, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy)
        VALUES (@QueryKey, @KqlText, @NextVersion, @Description, @SourceRef, @IsBuiltIn, @Enabled,
                SYSDATETIMEOFFSET(), @ChangedBy);

    INSERT INTO monitoring.KqlQueryAudit (QueryKey, Version, KqlText, Description, SourceRef, Enabled, Removed, ChangedBy)
    VALUES (@QueryKey, @NextVersion, @KqlText, @Description, @SourceRef, @Enabled, 0, @ChangedBy);

    SELECT QueryKey, KqlText, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy
    FROM monitoring.KqlQuery
    WHERE QueryKey = @QueryKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_DeleteKqlQuery
    @QueryKey nvarchar(128), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Version int = ISNULL((SELECT Version FROM monitoring.KqlQuery WHERE QueryKey = @QueryKey), 0);

    DELETE FROM monitoring.KqlQuery WHERE QueryKey = @QueryKey;

    -- Recorded even when nothing was deleted: "someone reverted this to the built-in" is history.
    INSERT INTO monitoring.KqlQueryAudit (QueryKey, Version, KqlText, Description, SourceRef, Enabled, Removed, ChangedBy)
    VALUES (@QueryKey, @Version, NULL, NULL, NULL, NULL, 1, @ChangedBy);
END
GO

-- Text excluded: the history list shows who/when/version, and the body is fetched per version
-- by usp_GetKqlQueryAuditEntry when someone opens a diff or restores.
CREATE OR ALTER PROCEDURE monitoring.usp_GetKqlQueryAudit
    @QueryKey nvarchar(128) = NULL,
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) AuditId, QueryKey, Version, Description, SourceRef, Enabled, Removed,
           ChangedAt, ChangedBy, CAST(ISNULL(DATALENGTH(KqlText), 0) / 2 AS int) AS TextLength
    FROM monitoring.KqlQueryAudit
    WHERE @QueryKey IS NULL OR QueryKey = @QueryKey
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKqlQueryAuditEntry
    @AuditId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AuditId, QueryKey, Version, KqlText, Description, SourceRef, Enabled, Removed, ChangedAt, ChangedBy
    FROM monitoring.KqlQueryAudit
    WHERE AuditId = @AuditId;
END
GO
