-- Who fixes a failing service. Grain: one row per (Vertical, Stage) in monitoring.ServiceOwner,
-- with ServiceOwnerAudit as append-only history - same shape as monitoring.AgentPauseSetting(+Audit).
--
-- Why (Vertical, Stage) and not the club: the club says WHO IS AFFECTED, the service says WHO FIXES
-- IT. A latency spike at "Hotels / 04 Hotel Details" belongs to the Hotel front-end team whichever
-- club was browsing, and routing by club would page a brand manager for a code defect.
--
-- Stage N'*' is the vertical-wide fallback, so a vertical can be covered by one row before every
-- stage has a named owner.
--
-- THIS TABLE STARTS EMPTY ON PURPOSE. The named primary and backup responders for the pilot
-- services are a leadership decision that has not been made yet (Platform Reliability Strategy V3,
-- Decision 3). Seeding plausible-looking owners would turn an open organisational dependency into
-- an invisible one, and the first time an alert routed to an invented name the whole routing layer
-- would stop being trusted. An unresolved owner is reported as unresolved.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.ServiceOwner', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ServiceOwner
    (
        -- Lower-cased vertical as the board emits it ("hotels", "cruises").
        Vertical     nvarchar(64)      NOT NULL,
        -- Journey stage as the classifier emits it ("04 Hotel Details"), or N'*' for every stage.
        Stage        nvarchar(128)     NOT NULL,
        -- The accountable team. Required: an owner with a person but no team disappears when that person leaves.
        OwnerTeam    nvarchar(200)     NOT NULL,
        -- Named primary and backup responder. NULL until Decision 3 names them; the team alone is still routable.
        -- Bracketed because BACKUP is a T-SQL keyword and an unquoted column of that name is a syntax error.
        Primary_     nvarchar(200)     NULL,
        [Backup]       nvarchar(200)     NULL,
        -- Where the alert actually goes (Teams channel, PagerDuty service, distribution list).
        ContactRef   nvarchar(400)     NULL,
        Notes        nvarchar(1000)    NULL,
        UpdatedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ServiceOwner_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_ServiceOwner_UpdatedBy DEFAULT N'',
        CONSTRAINT PK_monitoring_ServiceOwner PRIMARY KEY (Vertical, Stage)
    );
END
GO

IF OBJECT_ID(N'monitoring.ServiceOwnerAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ServiceOwnerAudit
    (
        AuditId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_ServiceOwnerAudit PRIMARY KEY,
        Vertical   nvarchar(64)      NOT NULL,
        Stage      nvarchar(128)     NOT NULL,
        OwnerTeam  nvarchar(200)     NULL,
        Primary_   nvarchar(200)     NULL,
        [Backup]     nvarchar(200)     NULL,
        ContactRef nvarchar(400)     NULL,
        -- Set when the row was removed, so "nobody owns this any more" is a recorded event and not a gap.
        Removed    bit               NOT NULL
            CONSTRAINT DF_monitoring_ServiceOwnerAudit_Removed DEFAULT 0,
        ChangedAt  datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ServiceOwnerAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy  nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_ServiceOwnerAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_ServiceOwnerAudit_Read
        ON monitoring.ServiceOwnerAudit (Vertical, Stage, ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllServiceOwners
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Vertical, Stage, OwnerTeam, Primary_ AS PrimaryResponder, [Backup], ContactRef, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.ServiceOwner
    ORDER BY Vertical, Stage;
END
GO

-- MERGE WITH (HOLDLOCK) rather than IF NOT EXISTS + UPDATE: two concurrent writers on the same
-- (Vertical, Stage) would otherwise race between the check and the insert.
CREATE OR ALTER PROCEDURE monitoring.usp_UpsertServiceOwner
    @Vertical nvarchar(64), @Stage nvarchar(128), @OwnerTeam nvarchar(200),
    @PrimaryResponder nvarchar(200), @Backup nvarchar(200), @ContactRef nvarchar(400),
    @Notes nvarchar(1000), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    MERGE monitoring.ServiceOwner WITH (HOLDLOCK) AS target
    USING (SELECT @Vertical AS Vertical, @Stage AS Stage) AS source
        ON target.Vertical = source.Vertical AND target.Stage = source.Stage
    WHEN MATCHED THEN
        UPDATE SET OwnerTeam = @OwnerTeam, Primary_ = @PrimaryResponder, [Backup] = @Backup,
                   ContactRef = @ContactRef, Notes = @Notes,
                   UpdatedAt = SYSDATETIMEOFFSET(), UpdatedBy = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (Vertical, Stage, OwnerTeam, Primary_, [Backup], ContactRef, Notes, UpdatedAt, UpdatedBy)
        VALUES (@Vertical, @Stage, @OwnerTeam, @PrimaryResponder, @Backup, @ContactRef, @Notes,
                SYSDATETIMEOFFSET(), @ChangedBy);

    INSERT INTO monitoring.ServiceOwnerAudit (Vertical, Stage, OwnerTeam, Primary_, [Backup], ContactRef, Removed, ChangedBy)
    VALUES (@Vertical, @Stage, @OwnerTeam, @PrimaryResponder, @Backup, @ContactRef, 0, @ChangedBy);

    SELECT Vertical, Stage, OwnerTeam, Primary_ AS PrimaryResponder, [Backup], ContactRef, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.ServiceOwner
    WHERE Vertical = @Vertical AND Stage = @Stage;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_DeleteServiceOwner
    @Vertical nvarchar(64), @Stage nvarchar(128), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM monitoring.ServiceOwner WHERE Vertical = @Vertical AND Stage = @Stage;

    -- Recorded even when nothing was deleted: "someone tried to unassign this" is still history.
    INSERT INTO monitoring.ServiceOwnerAudit (Vertical, Stage, OwnerTeam, Primary_, [Backup], ContactRef, Removed, ChangedBy)
    VALUES (@Vertical, @Stage, NULL, NULL, NULL, NULL, 1, @ChangedBy);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetServiceOwnerAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) AuditId, Vertical, Stage, OwnerTeam, Primary_ AS PrimaryResponder, [Backup],
           ContactRef, Removed, ChangedAt, ChangedBy
    FROM monitoring.ServiceOwnerAudit
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO
