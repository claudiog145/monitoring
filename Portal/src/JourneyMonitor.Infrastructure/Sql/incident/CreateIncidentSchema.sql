-- Exception Management incident store. WI 238073.
-- Six tables in a shared incident schema. Journey tables (hotel/car/cruise/air) are not
-- modified and are not referenced by a foreign key. incident.JourneyLink stores the domain
-- and the member or correlation id so a later query can find the journey.
--
-- Grain: one incident.Incident per club. The same error in two clubs is two incidents.
-- Grouping key: (ClubId, Environment, Fingerprint).
-- Fingerprint is the full Domain|ClubId|Environment|ExceptionType|Operation key (nvarchar(612)).
-- incident.Occurrence.ItemId is the Application Insights item id and stops the same event
-- from being stored twice when sync windows overlap.
--
-- usp_IngestExceptions writes QueryRun, Incident, Occurrence and JourneyLink.
-- Activity and ExternalReference are triage (someone assigns, changes status, or files a defect)
-- and are not written by ingestion.
--
-- Contract v6 §3: QueryRun, Incident and JourneyLink carry ProfileKey (default 'arrivia', the incumbent,
-- so rows written before it existed keep their meaning). The dedupe key is
-- (ProfileKey, ClubId, Environment, Fingerprint). Only the incumbent is held to the
-- hotels/cruises/cars/air domain list; another company's Domain is one of its own service keys.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'incident')
    EXEC(N'CREATE SCHEMA incident');
GO

IF OBJECT_ID(N'incident.QueryRun', N'U') IS NULL
BEGIN
    CREATE TABLE incident.QueryRun
    (
        QueryRunId   uniqueidentifier  NOT NULL
            CONSTRAINT PK_incident_QueryRun PRIMARY KEY
            CONSTRAINT DF_incident_QueryRun_Id DEFAULT NEWSEQUENTIALID(),
        Domain        nvarchar(32)      NOT NULL,
        ClubId        nvarchar(32)      NOT NULL,
        Environment   nvarchar(32)      NOT NULL,
        Window        nvarchar(16)      NOT NULL,
        FetchedAt     datetimeoffset(7) NOT NULL
            CONSTRAINT DF_incident_QueryRun_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        [RowCount]    int               NOT NULL
            CONSTRAINT DF_incident_QueryRun_RowCount DEFAULT 0,
        Status        nvarchar(16)      NOT NULL,
        ProfileKey    nvarchar(128)     NOT NULL
            CONSTRAINT DF_incident_QueryRun_ProfileKey DEFAULT N'arrivia',
        CONSTRAINT CK_incident_QueryRun_Domain
            CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air')),
        CONSTRAINT CK_incident_QueryRun_RowCount CHECK ([RowCount] >= 0)
    );

    CREATE INDEX IX_incident_QueryRun_FetchedAt
        ON incident.QueryRun (FetchedAt DESC);
END
GO

IF OBJECT_ID(N'incident.Incident', N'U') IS NULL
BEGIN
    CREATE TABLE incident.Incident
    (
        IncidentId       bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_incident_Incident PRIMARY KEY,
        ExternalId       nvarchar(32)      NOT NULL,
        Fingerprint      nvarchar(612)     NOT NULL,
        Domain           nvarchar(32)      NOT NULL,
        ClubId           nvarchar(32)      NOT NULL,
        Environment      nvarchar(32)      NOT NULL,
        ExceptionType    nvarchar(256)     NOT NULL,
        Component        nvarchar(128)     NOT NULL,
        Operation        nvarchar(256)     NOT NULL,
        Message          nvarchar(1024)    NOT NULL,
        Severity         nvarchar(16)      NOT NULL,
        Status           nvarchar(32)      NOT NULL,
        AssignedTo       nvarchar(128)     NULL,
        FirstSeen        datetimeoffset(7) NOT NULL,
        LastSeen         datetimeoffset(7) NOT NULL,
        OccurrenceCount  int               NOT NULL
            CONSTRAINT DF_incident_Incident_OccurrenceCount DEFAULT 0,
        UsersAffected    int               NOT NULL
            CONSTRAINT DF_incident_Incident_UsersAffected DEFAULT 0,
        LastQueryRunId   uniqueidentifier  NOT NULL,
        Source           nvarchar(16)      NOT NULL
            CONSTRAINT DF_incident_Incident_Source DEFAULT N'Server',
        ProfileKey       nvarchar(128)     NOT NULL
            CONSTRAINT DF_incident_Incident_ProfileKey DEFAULT N'arrivia',
        CONSTRAINT UQ_incident_Incident_ExternalId UNIQUE (ExternalId),
        CONSTRAINT UQ_incident_Incident_Fingerprint UNIQUE (ProfileKey, ClubId, Environment, Fingerprint),
        CONSTRAINT FK_incident_Incident_QueryRun
            FOREIGN KEY (LastQueryRunId) REFERENCES incident.QueryRun (QueryRunId),
        CONSTRAINT CK_incident_Incident_Domain
            CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air')),
        CONSTRAINT CK_incident_Incident_OccurrenceCount CHECK (OccurrenceCount >= 0),
        CONSTRAINT CK_incident_Incident_UsersAffected CHECK (UsersAffected >= 0),
        CONSTRAINT CK_incident_Incident_Source CHECK (Source IN (N'Server', N'Browser'))
    );

    CREATE INDEX IX_incident_Incident_Queue
        ON incident.Incident (Status, Severity, LastSeen DESC);
END
GO

-- Existing databases created the column at nvarchar(200). Widen it in place so a long
-- exception type plus operation still fits the unique key. 612 nvarchar chars are 1224 bytes.
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'incident.Incident')
      AND name = N'Fingerprint'
      AND max_length > 0
      AND max_length < 1224
)
BEGIN
    ALTER TABLE incident.Incident DROP CONSTRAINT UQ_incident_Incident_Fingerprint;
    ALTER TABLE incident.Incident ALTER COLUMN Fingerprint nvarchar(612) NOT NULL;
    ALTER TABLE incident.Incident ADD CONSTRAINT UQ_incident_Incident_Fingerprint
        UNIQUE (ClubId, Environment, Fingerprint);
END
GO

IF OBJECT_ID(N'incident.Occurrence', N'U') IS NULL
BEGIN
    CREATE TABLE incident.Occurrence
    (
        OccurrenceId   bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_incident_Occurrence PRIMARY KEY,
        IncidentId     bigint            NOT NULL,
        QueryRunId     uniqueidentifier  NOT NULL,
        OccurredAt     datetimeoffset(7) NOT NULL,
        MemberId       nvarchar(128)     NULL,
        SessionId      nvarchar(128)     NULL,
        CorrelationId  nvarchar(128)     NULL,
        OperationId    nvarchar(128)     NOT NULL,
        Url            nvarchar(2048)    NULL,
        Message        nvarchar(1024)    NOT NULL,
        StackTrace     nvarchar(max)     NULL,
        ItemId         nvarchar(128)     NOT NULL,
        Source         nvarchar(16)      NOT NULL
            CONSTRAINT DF_incident_Occurrence_Source DEFAULT N'Server',
        CONSTRAINT UQ_incident_Occurrence_ItemId UNIQUE (ItemId),
        CONSTRAINT CK_incident_Occurrence_Source CHECK (Source IN (N'Server', N'Browser')),
        CONSTRAINT FK_incident_Occurrence_Incident
            FOREIGN KEY (IncidentId) REFERENCES incident.Incident (IncidentId),
        CONSTRAINT FK_incident_Occurrence_QueryRun
            FOREIGN KEY (QueryRunId) REFERENCES incident.QueryRun (QueryRunId)
    );

    CREATE INDEX IX_incident_Occurrence_Incident
        ON incident.Occurrence (IncidentId, OccurredAt DESC);
END
GO

IF OBJECT_ID(N'incident.JourneyLink', N'U') IS NULL
BEGIN
    CREATE TABLE incident.JourneyLink
    (
        JourneyLinkId  bigint         NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_incident_JourneyLink PRIMARY KEY,
        IncidentId     bigint         NOT NULL,
        Domain         nvarchar(32)   NOT NULL,
        JourneyKey     nvarchar(128)  NOT NULL,
        JourneyId      bigint         NULL,
        ProfileKey     nvarchar(128)  NOT NULL
            CONSTRAINT DF_incident_JourneyLink_ProfileKey DEFAULT N'arrivia',
        CONSTRAINT UQ_incident_JourneyLink UNIQUE (IncidentId, Domain, JourneyKey),
        CONSTRAINT FK_incident_JourneyLink_Incident
            FOREIGN KEY (IncidentId) REFERENCES incident.Incident (IncidentId),
        CONSTRAINT CK_incident_JourneyLink_Domain
            CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air'))
    );
END
GO

IF OBJECT_ID(N'incident.Activity', N'U') IS NULL
BEGIN
    CREATE TABLE incident.Activity
    (
        ActivityId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_incident_Activity PRIMARY KEY,
        IncidentId    bigint            NOT NULL,
        At            datetimeoffset(7) NOT NULL
            CONSTRAINT DF_incident_Activity_At DEFAULT SYSDATETIMEOFFSET(),
        Actor         nvarchar(128)     NOT NULL,
        ActivityType  nvarchar(32)      NOT NULL,
        FromValue     nvarchar(128)     NULL,
        ToValue       nvarchar(128)     NULL,
        Note          nvarchar(1024)    NULL,
        CONSTRAINT FK_incident_Activity_Incident
            FOREIGN KEY (IncidentId) REFERENCES incident.Incident (IncidentId)
    );

    CREATE INDEX IX_incident_Activity_Incident
        ON incident.Activity (IncidentId, At DESC);
END
GO

IF OBJECT_ID(N'incident.ExternalReference', N'U') IS NULL
BEGIN
    CREATE TABLE incident.ExternalReference
    (
        ReferenceId  bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_incident_ExternalReference PRIMARY KEY,
        IncidentId   bigint            NOT NULL,
        RefType      nvarchar(32)      NOT NULL,
        RefValue     nvarchar(256)     NOT NULL,
        Url          nvarchar(1024)    NULL,
        State        nvarchar(64)      NULL,
        LinkedAt     datetimeoffset(7) NOT NULL
            CONSTRAINT DF_incident_ExternalReference_LinkedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT UQ_incident_ExternalReference UNIQUE (IncidentId, RefType, RefValue),
        CONSTRAINT FK_incident_ExternalReference_Incident
            FOREIGN KEY (IncidentId) REFERENCES incident.Incident (IncidentId)
    );
END
GO

-- Databases created before Source existed get the column here. New installs already have it
-- from the CREATE TABLE above. Existing rows are Server until a later sync stores client_Type.
-- The incident value follows the latest occurrence.
IF COL_LENGTH(N'incident.Incident', N'Source') IS NULL
BEGIN
    ALTER TABLE incident.Incident ADD
        Source nvarchar(16) NOT NULL
            CONSTRAINT DF_incident_Incident_Source DEFAULT N'Server'
            CONSTRAINT CK_incident_Incident_Source CHECK (Source IN (N'Server', N'Browser'));
END
GO

IF COL_LENGTH(N'incident.Occurrence', N'Source') IS NULL
BEGIN
    ALTER TABLE incident.Occurrence ADD
        Source nvarchar(16) NOT NULL
            CONSTRAINT DF_incident_Occurrence_Source DEFAULT N'Server'
            CONSTRAINT CK_incident_Occurrence_Source CHECK (Source IN (N'Server', N'Browser'));
END
GO

-- Contract v6 §3. Databases created before ProfileKey existed get it here; every existing row is the
-- incumbent's. Each step is guarded, so a re-run changes nothing.
IF COL_LENGTH(N'incident.QueryRun', N'ProfileKey') IS NULL
    ALTER TABLE incident.QueryRun ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_incident_QueryRun_ProfileKey DEFAULT N'arrivia';
GO

IF COL_LENGTH(N'incident.Incident', N'ProfileKey') IS NULL
    ALTER TABLE incident.Incident ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_incident_Incident_ProfileKey DEFAULT N'arrivia';
GO

IF COL_LENGTH(N'incident.JourneyLink', N'ProfileKey') IS NULL
    ALTER TABLE incident.JourneyLink ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_incident_JourneyLink_ProfileKey DEFAULT N'arrivia';
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_QueryRun_Domain'
           AND parent_object_id = OBJECT_ID(N'incident.QueryRun') AND definition NOT LIKE N'%ProfileKey%')
    ALTER TABLE incident.QueryRun DROP CONSTRAINT CK_incident_QueryRun_Domain;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_QueryRun_Domain'
               AND parent_object_id = OBJECT_ID(N'incident.QueryRun'))
    ALTER TABLE incident.QueryRun ADD CONSTRAINT CK_incident_QueryRun_Domain
        CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_Incident_Domain'
           AND parent_object_id = OBJECT_ID(N'incident.Incident') AND definition NOT LIKE N'%ProfileKey%')
    ALTER TABLE incident.Incident DROP CONSTRAINT CK_incident_Incident_Domain;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_Incident_Domain'
               AND parent_object_id = OBJECT_ID(N'incident.Incident'))
    ALTER TABLE incident.Incident ADD CONSTRAINT CK_incident_Incident_Domain
        CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air'));
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_JourneyLink_Domain'
           AND parent_object_id = OBJECT_ID(N'incident.JourneyLink') AND definition NOT LIKE N'%ProfileKey%')
    ALTER TABLE incident.JourneyLink DROP CONSTRAINT CK_incident_JourneyLink_Domain;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_incident_JourneyLink_Domain'
               AND parent_object_id = OBJECT_ID(N'incident.JourneyLink'))
    ALTER TABLE incident.JourneyLink ADD CONSTRAINT CK_incident_JourneyLink_Domain
        CHECK (ProfileKey <> N'arrivia' OR Domain IN (N'hotels', N'cruises', N'cars', N'air'));
GO

-- The dedupe key includes ProfileKey: the same error in two companies is two incidents.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_incident_Incident_Fingerprint'
      AND object_id = OBJECT_ID(N'incident.Incident'))
   AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.name = N'UQ_incident_Incident_Fingerprint'
      AND i.object_id = OBJECT_ID(N'incident.Incident')
      AND c.name = N'ProfileKey')
    ALTER TABLE incident.Incident DROP CONSTRAINT UQ_incident_Incident_Fingerprint;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_incident_Incident_Fingerprint'
      AND object_id = OBJECT_ID(N'incident.Incident'))
    ALTER TABLE incident.Incident ADD CONSTRAINT UQ_incident_Incident_Fingerprint
        UNIQUE (ProfileKey, ClubId, Environment, Fingerprint);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_incident_Incident_Profile'
      AND object_id = OBJECT_ID(N'incident.Incident'))
    CREATE INDEX IX_incident_Incident_Profile
        ON incident.Incident (ProfileKey, Status, Severity, LastSeen DESC);
GO

-- One call is one domain sync. A QueryRun is written per club that produced rows
-- (ClubId is required on QueryRun; one KQL run can see several clubs).
-- Existing incidents keep Status and AssignedTo. Counts and severity are recomputed
-- from incident.Occurrence. Severity bands match IncidentIngestionPlanner.SeverityForOccurrenceCount:
-- >= 100 Critical, >= 25 High, >= 5 Medium, otherwise Low.
CREATE OR ALTER PROCEDURE incident.usp_IngestExceptions
    @Domain           nvarchar(32),
    @Environment      nvarchar(32),
    @Window           nvarchar(16),
    @FallbackClubId   nvarchar(32),
    @IncidentsJson    nvarchar(max),
    @QueryRunCount        int = NULL OUTPUT,
    @InsertedIncidents    int = NULL OUTPUT,
    @UpdatedIncidents     int = NULL OUTPUT,
    @InsertedOccurrences  int = NULL OUTPUT,
    @ProfileKey           nvarchar(128) = N'arrivia'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Profile nvarchar(128) = ISNULL(NULLIF(LTRIM(RTRIM(@ProfileKey)), N''), N'arrivia');

    DECLARE @inc TABLE
    (
        Fingerprint   nvarchar(612)  NOT NULL,
        ClubId        nvarchar(32)   NOT NULL,
        ExceptionType nvarchar(256)  NOT NULL,
        Component     nvarchar(128)  NOT NULL,
        Operation     nvarchar(256)  NOT NULL,
        Message       nvarchar(1024) NOT NULL,
        FirstSeen     datetimeoffset(7) NOT NULL,
        LastSeen      datetimeoffset(7) NOT NULL,
        Occurrences   nvarchar(max)  NULL,
        JourneyKeys   nvarchar(max)  NULL
    );

    INSERT INTO @inc (Fingerprint, ClubId, ExceptionType, Component, Operation, Message, FirstSeen, LastSeen, Occurrences, JourneyKeys)
    SELECT
        Fingerprint, ClubId, ExceptionType, Component, Operation, Message, FirstSeen, LastSeen, Occurrences, JourneyKeys
    FROM OPENJSON(@IncidentsJson)
    WITH
    (
        Fingerprint   nvarchar(612)     N'$.Fingerprint',
        ClubId        nvarchar(32)      N'$.ClubId',
        ExceptionType nvarchar(256)     N'$.ExceptionType',
        Component     nvarchar(128)     N'$.Component',
        Operation     nvarchar(256)     N'$.Operation',
        Message       nvarchar(1024)    N'$.Message',
        FirstSeen     datetimeoffset(7) N'$.FirstSeen',
        LastSeen      datetimeoffset(7) N'$.LastSeen',
        Occurrences   nvarchar(max)     N'$.Occurrences' AS JSON,
        JourneyKeys   nvarchar(max)     N'$.JourneyKeys' AS JSON
    );

    DECLARE @run TABLE
    (
        ClubId     nvarchar(32)     NOT NULL PRIMARY KEY,
        QueryRunId uniqueidentifier NOT NULL
    );

    BEGIN TRANSACTION;

    INSERT INTO @run (ClubId, QueryRunId)
    SELECT ClubId, NEWID()
    FROM (SELECT DISTINCT ClubId FROM @inc) AS clubs;

    IF NOT EXISTS (SELECT 1 FROM @run)
    BEGIN
        INSERT INTO @run (ClubId, QueryRunId) VALUES (@FallbackClubId, NEWID());
    END

    INSERT INTO incident.QueryRun (QueryRunId, Domain, ClubId, Environment, Window, FetchedAt, [RowCount], Status, ProfileKey)
    SELECT QueryRunId, @Domain, ClubId, @Environment, @Window, @Now, 0, N'Succeeded', @Profile
    FROM @run;

    DECLARE @maxExternal int;
    SELECT @maxExternal = ISNULL(MAX(TRY_CONVERT(int, SUBSTRING(ExternalId, 5, 28))), 1999)
    FROM incident.Incident WITH (UPDLOCK, HOLDLOCK)
    WHERE ExternalId LIKE N'EXC-[0-9]%';

    DECLARE @new TABLE
    (
        Fingerprint nvarchar(612) NOT NULL,
        ClubId      nvarchar(32)  NOT NULL,
        Rn          int           NOT NULL
    );

    -- One row per club and fingerprint. The unique index is case-insensitive, so two
    -- JSON rows that differ only by case would both look new and the second insert would fail.
    INSERT INTO @new (Fingerprint, ClubId, Rn)
    SELECT d.Fingerprint, d.ClubId, ROW_NUMBER() OVER (ORDER BY d.ClubId, d.Fingerprint)
    FROM
    (
        SELECT
            Fingerprint,
            ClubId,
            ROW_NUMBER() OVER (PARTITION BY ClubId, Fingerprint ORDER BY FirstSeen, ExceptionType, Operation) AS KeepRow
        FROM @inc
    ) AS d
    WHERE d.KeepRow = 1
      AND NOT EXISTS
      (
          SELECT 1
          FROM incident.Incident AS existing
          WHERE existing.ClubId = d.ClubId
            AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
            AND existing.Fingerprint = d.Fingerprint
      );

    INSERT INTO incident.Incident
    (
        ExternalId, Fingerprint, Domain, ClubId, Environment, ExceptionType, Component, Operation, Message,
        Severity, Status, AssignedTo, FirstSeen, LastSeen, OccurrenceCount, UsersAffected, LastQueryRunId, ProfileKey
    )
    SELECT
        CONCAT(N'EXC-', @maxExternal + n.Rn),
        i.Fingerprint, @Domain, i.ClubId, @Environment, i.ExceptionType, i.Component, i.Operation, i.Message,
        N'Low', N'Open', NULL, i.FirstSeen, i.LastSeen, 0, 0, r.QueryRunId, @Profile
    FROM @new AS n
    INNER JOIN
    (
        SELECT
            *,
            ROW_NUMBER() OVER (PARTITION BY ClubId, Fingerprint ORDER BY FirstSeen, ExceptionType, Operation) AS KeepRow
        FROM @inc
    ) AS i ON i.ClubId = n.ClubId AND i.Fingerprint = n.Fingerprint AND i.KeepRow = 1
    INNER JOIN @run AS r ON r.ClubId = i.ClubId;

    SET @InsertedIncidents = @@ROWCOUNT;

    UPDATE existing
    SET
        FirstSeen = CASE WHEN i.FirstSeen < existing.FirstSeen THEN i.FirstSeen ELSE existing.FirstSeen END,
        LastSeen = CASE WHEN i.LastSeen > existing.LastSeen THEN i.LastSeen ELSE existing.LastSeen END,
        LastQueryRunId = r.QueryRunId
    FROM incident.Incident AS existing
    INNER JOIN @inc AS i
        ON i.ClubId = existing.ClubId
       AND i.Fingerprint = existing.Fingerprint
       AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
    INNER JOIN @run AS r ON r.ClubId = i.ClubId
    WHERE NOT EXISTS (SELECT 1 FROM @new AS n WHERE n.ClubId = i.ClubId AND n.Fingerprint = i.Fingerprint);

    SET @UpdatedIncidents = @@ROWCOUNT;

    -- A later sync can fill a member id the first sighting did not have. Users Affected
    -- is recomputed from these rows below, so a blank id would keep that count at 0.
    UPDATE already
    SET MemberId = NULLIF(o.MemberId, N'')
    FROM @inc AS i
    INNER JOIN incident.Incident AS existing
        ON existing.ClubId = i.ClubId
       AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
       AND existing.Fingerprint = i.Fingerprint
    CROSS APPLY OPENJSON(i.Occurrences)
    WITH
    (
        MemberId nvarchar(128) N'$.MemberId',
        ItemId   nvarchar(128) N'$.ItemId'
    ) AS o
    INNER JOIN incident.Occurrence AS already
        ON already.ItemId = o.ItemId
       AND already.IncidentId = existing.IncidentId
    WHERE NULLIF(already.MemberId, N'') IS NULL
      AND NULLIF(o.MemberId, N'') IS NOT NULL;

    INSERT INTO incident.Occurrence
    (
        IncidentId, QueryRunId, OccurredAt, MemberId, SessionId, CorrelationId,
        OperationId, Url, Message, StackTrace, ItemId, Source
    )
    SELECT
        existing.IncidentId, r.QueryRunId, o.OccurredAt, NULLIF(o.MemberId, N''), NULLIF(o.SessionId, N''),
        NULLIF(o.CorrelationId, N''), o.OperationId, NULLIF(o.Url, N''), o.Message, o.StackTrace, o.ItemId,
        CASE WHEN o.Source = N'Browser' THEN N'Browser' ELSE N'Server' END
    FROM @inc AS i
    INNER JOIN incident.Incident AS existing
        ON existing.ClubId = i.ClubId
       AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
       AND existing.Fingerprint = i.Fingerprint
    INNER JOIN @run AS r ON r.ClubId = i.ClubId
    CROSS APPLY OPENJSON(i.Occurrences)
    WITH
    (
        OccurredAt    datetimeoffset(7) N'$.OccurredAt',
        MemberId      nvarchar(128)     N'$.MemberId',
        SessionId     nvarchar(128)     N'$.SessionId',
        CorrelationId nvarchar(128)     N'$.CorrelationId',
        OperationId   nvarchar(128)     N'$.OperationId',
        Url           nvarchar(2048)    N'$.Url',
        Message       nvarchar(1024)    N'$.Message',
        StackTrace    nvarchar(max)     N'$.StackTrace',
        ItemId        nvarchar(128)     N'$.ItemId',
        Source        nvarchar(16)      N'$.Source'
    ) AS o
    WHERE NOT EXISTS (SELECT 1 FROM incident.Occurrence AS already WHERE already.ItemId = o.ItemId);

    SET @InsertedOccurrences = @@ROWCOUNT;

    INSERT INTO incident.JourneyLink (IncidentId, Domain, JourneyKey, JourneyId, ProfileKey)
    SELECT DISTINCT existing.IncidentId, @Domain, keys.value, NULL, @Profile
    FROM @inc AS i
    INNER JOIN incident.Incident AS existing
        ON existing.ClubId = i.ClubId
       AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
       AND existing.Fingerprint = i.Fingerprint
    CROSS APPLY OPENJSON(i.JourneyKeys) AS keys
    WHERE keys.value <> N''
      AND NOT EXISTS
      (
          SELECT 1
          FROM incident.JourneyLink AS link
          WHERE link.IncidentId = existing.IncidentId
            AND link.Domain = @Domain
            AND link.JourneyKey = keys.value
      );

    UPDATE existing
    SET
        OccurrenceCount = counts.OccurrenceCount,
        UsersAffected = counts.UsersAffected,
        FirstSeen = COALESCE(counts.FirstSeen, existing.FirstSeen),
        LastSeen = COALESCE(counts.LastSeen, existing.LastSeen),
        Severity = CASE
            WHEN counts.OccurrenceCount >= 100 THEN N'Critical'
            WHEN counts.OccurrenceCount >= 25 THEN N'High'
            WHEN counts.OccurrenceCount >= 5 THEN N'Medium'
            ELSE N'Low'
        END,
        Source = COALESCE(counts.LatestSource, existing.Source)
    FROM incident.Incident AS existing
    INNER JOIN @inc AS i
        ON i.ClubId = existing.ClubId
       AND i.Fingerprint = existing.Fingerprint
       AND existing.Environment = @Environment AND existing.ProfileKey = @Profile
    CROSS APPLY
    (
        SELECT
            COUNT(*) AS OccurrenceCount,
            COUNT(DISTINCT NULLIF(o.MemberId, N'')) AS UsersAffected,
            MIN(o.OccurredAt) AS FirstSeen,
            MAX(o.OccurredAt) AS LastSeen,
            (
                SELECT TOP (1) src.Source
                FROM incident.Occurrence AS src
                WHERE src.IncidentId = existing.IncidentId
                ORDER BY src.OccurredAt DESC, src.OccurrenceId DESC
            ) AS LatestSource
        FROM incident.Occurrence AS o
        WHERE o.IncidentId = existing.IncidentId
    ) AS counts;

    UPDATE q
    SET [RowCount] = ISNULL(rows.Cnt, 0)
    FROM incident.QueryRun AS q
    INNER JOIN @run AS r ON r.QueryRunId = q.QueryRunId
    OUTER APPLY
    (
        SELECT COUNT(*) AS Cnt
        FROM @inc AS i
        CROSS APPLY OPENJSON(i.Occurrences) AS o
        WHERE i.ClubId = r.ClubId
    ) AS rows;

    SET @QueryRunCount = (SELECT COUNT(*) FROM @run);

    COMMIT TRANSACTION;
END
GO
