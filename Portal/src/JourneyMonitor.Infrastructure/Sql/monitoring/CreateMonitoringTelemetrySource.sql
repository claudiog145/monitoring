-- Where telemetry is read from. Grain: one row per (Alias, Environment) in monitoring.TelemetrySource,
-- with TelemetrySourceAudit as append-only history - same shape as monitoring.ServiceOwner(+Audit).
--
-- Why this table exists: 60 Application Insights ARM resource ids were hardcoded across 11 files
-- (8 KQL texts, InsightsSourceResolver.cs and the journey queries). Changing subscription meant
-- editing those files and redeploying, and 41 of the 60 had no configuration override at all -
-- a query left pointing at the old resource returns 403 or zero rows, and an empty board looks
-- exactly like "no problems". The KQL now says app('{{SOURCE:cruise-core}}') and the alias is
-- resolved against this table for the requested environment.
--
-- Alias is the stable name the KQL refers to; ResourceId is what actually changes between
-- subscriptions. Kind has no CHECK on purpose: 'app-insights' is all we query today, but
-- AppInsightsTelemetrySource already anticipates a Fabric/Eventhouse sibling, and a CHECK here
-- would make adding one a schema migration instead of a row.
--
-- This table is also the allow-list: KqlSourceResolver rejects a literal /subscriptions/... in
-- query text, so a stored query can only ever reach a resource that was registered here.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.TelemetrySource', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.TelemetrySource
    (
        -- The name used inside KQL as {{SOURCE:alias}}. Lower-cased by the store.
        Alias        nvarchar(64)      NOT NULL,
        Environment  nvarchar(32)      NOT NULL,
        -- Backend family. See the header: deliberately unconstrained.
        Kind         nvarchar(32)      NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySource_Kind DEFAULT N'app-insights',
        -- The ARM resource id substituted into app('...'). Validated as an ARM id before use.
        ResourceId   nvarchar(512)     NOT NULL,
        -- Application id for the REST endpoint (/v1/apps/{AppId}/query). Null when this source is
        -- only ever reached cross-resource from another source's query.
        AppId        nvarchar(64)      NULL,
        TenantId     nvarchar(64)      NULL,
        Enabled      bit               NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySource_Enabled DEFAULT 1,
        Notes        nvarchar(1000)    NULL,
        UpdatedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySource_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySource_UpdatedBy DEFAULT N'',
        CONSTRAINT PK_monitoring_TelemetrySource PRIMARY KEY (Alias, Environment),
        CONSTRAINT CK_monitoring_TelemetrySource_Environment
            CHECK (Environment IN (N'production', N'staging', N'development'))
    );
END
GO

IF OBJECT_ID(N'monitoring.TelemetrySourceAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.TelemetrySourceAudit
    (
        AuditId      bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_TelemetrySourceAudit PRIMARY KEY,
        Alias        nvarchar(64)      NOT NULL,
        Environment  nvarchar(32)      NOT NULL,
        Kind         nvarchar(32)      NULL,
        ResourceId   nvarchar(512)     NULL,
        AppId        nvarchar(64)      NULL,
        TenantId     nvarchar(64)      NULL,
        Enabled      bit               NULL,
        Notes        nvarchar(1000)    NULL,
        -- Set when the row was removed, so "this source is gone" is a recorded event and not a gap.
        Removed      bit               NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySourceAudit_Removed DEFAULT 0,
        ChangedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySourceAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_TelemetrySourceAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_TelemetrySourceAudit_Read
        ON monitoring.TelemetrySourceAudit (Alias, Environment, ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllTelemetrySources
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.TelemetrySource
    ORDER BY Alias, Environment;
END
GO

-- MERGE WITH (HOLDLOCK) rather than IF NOT EXISTS + UPDATE: two concurrent writers on the same
-- (Alias, Environment) would otherwise race between the check and the insert.
CREATE OR ALTER PROCEDURE monitoring.usp_UpsertTelemetrySource
    @Alias nvarchar(64), @Environment nvarchar(32), @Kind nvarchar(32),
    @ResourceId nvarchar(512), @AppId nvarchar(64), @TenantId nvarchar(64),
    @Enabled bit, @Notes nvarchar(1000), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    MERGE monitoring.TelemetrySource WITH (HOLDLOCK) AS target
    USING (SELECT @Alias AS Alias, @Environment AS Environment) AS source
        ON target.Alias = source.Alias AND target.Environment = source.Environment
    WHEN MATCHED THEN
        UPDATE SET Kind = @Kind, ResourceId = @ResourceId, AppId = @AppId, TenantId = @TenantId,
                   Enabled = @Enabled, Notes = @Notes,
                   UpdatedAt = SYSDATETIMEOFFSET(), UpdatedBy = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, UpdatedAt, UpdatedBy)
        VALUES (@Alias, @Environment, @Kind, @ResourceId, @AppId, @TenantId, @Enabled, @Notes,
                SYSDATETIMEOFFSET(), @ChangedBy);

    INSERT INTO monitoring.TelemetrySourceAudit (Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, Removed, ChangedBy)
    VALUES (@Alias, @Environment, @Kind, @ResourceId, @AppId, @TenantId, @Enabled, @Notes, 0, @ChangedBy);

    SELECT Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.TelemetrySource
    WHERE Alias = @Alias AND Environment = @Environment;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_DeleteTelemetrySource
    @Alias nvarchar(64), @Environment nvarchar(32), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM monitoring.TelemetrySource WHERE Alias = @Alias AND Environment = @Environment;

    -- Recorded even when nothing was deleted: "someone tried to remove this source" is still history.
    INSERT INTO monitoring.TelemetrySourceAudit (Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, Removed, ChangedBy)
    VALUES (@Alias, @Environment, NULL, NULL, NULL, NULL, NULL, NULL, 1, @ChangedBy);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetTelemetrySourceAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) AuditId, Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled,
           Notes, Removed, ChangedAt, ChangedBy
    FROM monitoring.TelemetrySourceAudit
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO

-- Seed: the resource ids that were hardcoded in InsightsSourceResolver.cs and the KQL texts, so a
-- fresh database behaves exactly like the pre-refactor build. INSERT-only on purpose - re-running
-- init-sql.sh must never overwrite a resource id an operator changed in the portal, which a
-- WHEN MATCHED branch would do on every container start.
MERGE monitoring.TelemetrySource WITH (HOLDLOCK) AS target
USING (VALUES
    (N'air-ui',       N'production', N'/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-UI-Prod-USC',      N'Air UI, cross-resource half of the Air journey query.'),
    (N'air-api',      N'production', N'/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-Prod-USC/providers/microsoft.insights/components/appInsights-Air-API-Prod-USC',     N'Air API, cross-resource half of the Air journey query.'),
    (N'air-ui',       N'staging',    N'/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-UAT-USC/providers/microsoft.insights/components/appInsights-Air-UI-UAT-USC',        N'Air UI (UAT).'),
    (N'air-api',      N'staging',    N'/subscriptions/819c105d-0328-4c63-a1a4-5284021e175f/resourceGroups/Air-RG-UAT-USC/providers/microsoft.insights/components/appInsights-Air-API-UAT-USC',       N'Air API (UAT).'),
    (N'club',         N'production', N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/ovs-club-dev/providers/microsoft.insights/components/appInsights-ovs-club',                N'Club front end. Resource group is named -dev but carries production traffic.'),
    (N'club',         N'staging',    N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/club-rg-uat/providers/microsoft.insights/components/club-ai-uat-usc',                      N'Club front end (UAT).'),
    (N'cruise-core',  N'production', N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-prod',     N'Cruise core services.'),
    (N'cruise-core',  N'staging',    N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-uat-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-uat',       N'Cruise core services (UAT).'),
    (N'cruise-api',   N'production', N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-web-api-ai-prod',      N'Cruise web API. No UAT twin registered yet.'),
    (N'sor-frontend', N'production', N'/subscriptions/89780439-fb9b-4f7b-bf37-115569216afe/resourceGroups/sor-appinsights-rg-usw/providers/microsoft.insights/components/sor-frontend-ai-prod-usw',  N'SaveOnResorts front end. No UAT twin registered yet.')
) AS source (Alias, Environment, ResourceId, Notes)
    ON target.Alias = source.Alias AND target.Environment = source.Environment
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Alias, Environment, Kind, ResourceId, Enabled, Notes, UpdatedBy)
    VALUES (source.Alias, source.Environment, N'app-insights', source.ResourceId, 1, source.Notes, N'seed');
GO
