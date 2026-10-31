-- Persists Findings, DoctorReports, per-monkey last-run timestamps, and the lightweight
-- self-bootstrapped knowledge base - previously all 4 were process-local (InMemory*), lost on
-- every container restart. Grain/keys mirror the Core domain records exactly (see
-- JourneyMonitor.Core/Domain/Monitoring.cs) so the Sql*Repository classes are thin Dapper
-- pass-throughs, no translation logic.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.Finding', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.Finding
    (
        Id              uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_Finding PRIMARY KEY,
        Domain          nvarchar(32)      NOT NULL,
        FindingType     nvarchar(64)      NOT NULL,
        Stage           nvarchar(128)     NOT NULL,
        Title           nvarchar(512)     NOT NULL,
        Severity        nvarchar(16)      NOT NULL,
        EvidenceJson    nvarchar(max)     NOT NULL,
        Status          nvarchar(16)      NOT NULL
            CONSTRAINT DF_monitoring_Finding_Status DEFAULT N'open',
        OriginMonkeyKey nvarchar(64)      NOT NULL,
        CreatedAt       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_Finding_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CorrelationId   nvarchar(256)     NOT NULL
    );

    CREATE INDEX IX_monitoring_Finding_CorrelationId_Status ON monitoring.Finding (CorrelationId, Status);
    CREATE INDEX IX_monitoring_Finding_CreatedAt ON monitoring.Finding (CreatedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.DoctorReport', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.DoctorReport
    (
        Id                  uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_DoctorReport PRIMARY KEY,
        FindingId           uniqueidentifier  NOT NULL,
        Verdict             nvarchar(1024)    NOT NULL,
        ErrorCategory       nvarchar(64)      NOT NULL,
        RootCauseHypothesis nvarchar(max)     NULL,
        Confidence          float             NOT NULL,
        CitationsJson       nvarchar(max)     NULL,
        ProposedFixJson     nvarchar(max)     NULL,
        ModelUsed           nvarchar(128)     NOT NULL,
        TokensIn            int               NOT NULL CONSTRAINT DF_monitoring_DoctorReport_TokensIn DEFAULT 0,
        TokensOut           int               NOT NULL CONSTRAINT DF_monitoring_DoctorReport_TokensOut DEFAULT 0,
        GeneratedAt         datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_DoctorReport_GeneratedAt DEFAULT SYSDATETIMEOFFSET(),
        HumanFeedback       nvarchar(32)      NULL
    );

    CREATE INDEX IX_monitoring_DoctorReport_FindingId ON monitoring.DoctorReport (FindingId, GeneratedAt DESC);
    CREATE INDEX IX_monitoring_DoctorReport_GeneratedAt ON monitoring.DoctorReport (GeneratedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.MonkeyRun', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.MonkeyRun
    (
        MonkeyKey nvarchar(64)      NOT NULL CONSTRAINT PK_monitoring_MonkeyRun PRIMARY KEY,
        LastRunAt datetimeoffset(7) NOT NULL
    );
END
GO

IF OBJECT_ID(N'monitoring.KnowledgeChunk', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KnowledgeChunk
    (
        Id            uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_KnowledgeChunk PRIMARY KEY,
        SourceType    nvarchar(64)      NOT NULL,
        SourceRef     nvarchar(128)     NULL,
        Content       nvarchar(max)     NOT NULL,
        EmbeddingJson nvarchar(max)     NOT NULL,
        CreatedAt     datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_KnowledgeChunk_CreatedAt DEFAULT SYSDATETIMEOFFSET()
    );
END
GO

-- Which model produced EmbeddingJson (2026-10-15). Retrieval compares a chunk only against
-- vectors from the SAME model: cosine similarity across two models is arithmetic without meaning,
-- and with different dimensions it cannot be computed at all. Added by ALTER because the CREATE
-- TABLE above never runs again on an already-provisioned database.
--
-- Left NULL for rows written before this column existed; SqlKnowledgeRetriever maps NULL to
-- 'unknown', which never matches a live model, so legacy chunks drop out of retrieval instead of
-- being silently compared as if they were produced by whatever model is current now.
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'EmbeddingModel') IS NULL
    ALTER TABLE monitoring.KnowledgeChunk ADD EmbeddingModel nvarchar(128) NULL;
GO

-- Imported-corpus metadata (2026-10-15). The knowledge base used to hold only this system's own
-- past diagnoses, which risks a model citing its own earlier guess as evidence. It now also holds
-- human-written RCAs and incident tickets, and a citation has to be able to name the document and
-- section a human can go and read.
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'Domain') IS NULL
    ALTER TABLE monitoring.KnowledgeChunk ADD Domain nvarchar(64) NULL;
GO
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'DocumentTitle') IS NULL
    ALTER TABLE monitoring.KnowledgeChunk ADD DocumentTitle nvarchar(400) NULL;
GO
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'SectionTitle') IS NULL
    ALTER TABLE monitoring.KnowledgeChunk ADD SectionTitle nvarchar(200) NULL;
GO
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'ContentHash') IS NULL
    ALTER TABLE monitoring.KnowledgeChunk ADD ContentHash char(64) NULL;
GO

-- Which company may retrieve this chunk (2026-10-29). Two values with two different meanings:
--   N''              - SHARED corpus: visible to every company.
--   a concrete key   - visible ONLY to that company.
-- Retrieval filters on (ProfileKey = @ProfileKey OR ProfileKey = N''), so a precedent is either
-- deliberately shared or deliberately scoped, and never reaches a company by accident.
--
-- The backfill lives INSIDE this guard on purpose, and it is the most delicate line of the change.
-- The chunks already stored are real RCA documents belonging to the incumbent company; leaving
-- them N'' would publish one customer's incident history to every company onboarded afterwards.
-- Running it here means it executes exactly once - in the deployment that creates the column - so
-- the next deploy cannot re-capture chunks that were since indexed as deliberately shared.
--
-- Dynamic SQL because SQL Server defers name resolution for a missing TABLE, never for a missing
-- COLUMN of a table that exists: an inline UPDATE would fail to parse in this same batch.
IF COL_LENGTH(N'monitoring.KnowledgeChunk', N'ProfileKey') IS NULL
BEGIN
    ALTER TABLE monitoring.KnowledgeChunk ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_KnowledgeChunk_ProfileKey DEFAULT N'';

    EXEC(N'UPDATE monitoring.KnowledgeChunk SET ProfileKey = N''arrivia'';');
END
GO

-- SourceRef now holds a document path ("MemberMaint/1.10.24 MemberMaint Outage ( No servers are
-- available for membermaint.) - Copy.md"), which overflows the original 128 characters sized for
-- a GUID. Only widens a genuinely narrower legacy column: nvarchar(max) reports -1 and must not
-- be narrowed, and a column already widened by this script reports 800.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'monitoring.KnowledgeChunk') AND name = N'SourceRef'
        AND max_length > 0 AND max_length < 800
)
    ALTER TABLE monitoring.KnowledgeChunk ALTER COLUMN SourceRef nvarchar(400) NULL;
GO

-- FILTERED unique index: eleven documents in the corpus are literal copies of each other
-- ("- Copy", "(1)"), and the same precedent indexed twice silently doubles its weight in cosine
-- retrieval. The filter is what lets pre-existing rows (self-bootstrapped findings, which have no
-- hash) keep living alongside it instead of all colliding on NULL.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_monitoring_KnowledgeChunk_ContentHash' AND object_id = OBJECT_ID(N'monitoring.KnowledgeChunk'))
    CREATE UNIQUE INDEX UQ_monitoring_KnowledgeChunk_ContentHash
        ON monitoring.KnowledgeChunk (ContentHash)
        WHERE ContentHash IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_KnowledgeChunk_SourceType' AND object_id = OBJECT_ID(N'monitoring.KnowledgeChunk'))
    CREATE INDEX IX_monitoring_KnowledgeChunk_SourceType
        ON monitoring.KnowledgeChunk (SourceType, Domain);
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddFinding
    @Id uniqueidentifier, @Domain nvarchar(32), @FindingType nvarchar(64), @Stage nvarchar(128),
    @Title nvarchar(512), @Severity nvarchar(16), @EvidenceJson nvarchar(max), @Status nvarchar(16),
    @OriginMonkeyKey nvarchar(64), @CreatedAt datetimeoffset(7), @CorrelationId nvarchar(256)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.Finding
        (Id, Domain, FindingType, Stage, Title, Severity, EvidenceJson, Status, OriginMonkeyKey, CreatedAt, CorrelationId)
    VALUES
        (@Id, @Domain, @FindingType, @Stage, @Title, @Severity, @EvidenceJson, @Status, @OriginMonkeyKey, @CreatedAt, @CorrelationId);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpdateFinding
    @Id uniqueidentifier, @Title nvarchar(512), @Severity nvarchar(16), @EvidenceJson nvarchar(max), @Status nvarchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.Finding
    SET Title = @Title, Severity = @Severity, EvidenceJson = @EvidenceJson, Status = @Status
    WHERE Id = @Id;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetOpenFindingByCorrelationId
    @CorrelationId nvarchar(256)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, Domain, FindingType, Stage, Title, Severity, EvidenceJson, Status, OriginMonkeyKey, CreatedAt, CorrelationId
    FROM monitoring.Finding
    WHERE CorrelationId = @CorrelationId AND Status = N'open'
    ORDER BY CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetOpenFindingsByDomain
    @Domain nvarchar(32)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Domain, FindingType, Stage, Title, Severity, EvidenceJson, Status, OriginMonkeyKey, CreatedAt, CorrelationId
    FROM monitoring.Finding
    WHERE Domain = @Domain AND Status = N'open'
    ORDER BY CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllFindings
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Domain, FindingType, Stage, Title, Severity, EvidenceJson, Status, OriginMonkeyKey, CreatedAt, CorrelationId
    FROM monitoring.Finding
    ORDER BY CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetFindingById
    @Id uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Domain, FindingType, Stage, Title, Severity, EvidenceJson, Status, OriginMonkeyKey, CreatedAt, CorrelationId
    FROM monitoring.Finding
    WHERE Id = @Id;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddDoctorReport
    @Id uniqueidentifier, @FindingId uniqueidentifier, @Verdict nvarchar(1024), @ErrorCategory nvarchar(64),
    @RootCauseHypothesis nvarchar(max) = NULL, @Confidence float, @CitationsJson nvarchar(max) = NULL,
    @ProposedFixJson nvarchar(max) = NULL, @ModelUsed nvarchar(128), @TokensIn int, @TokensOut int,
    @GeneratedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.DoctorReport
        (Id, FindingId, Verdict, ErrorCategory, RootCauseHypothesis, Confidence, CitationsJson, ProposedFixJson, ModelUsed, TokensIn, TokensOut, GeneratedAt)
    VALUES
        (@Id, @FindingId, @Verdict, @ErrorCategory, @RootCauseHypothesis, @Confidence, @CitationsJson, @ProposedFixJson, @ModelUsed, @TokensIn, @TokensOut, @GeneratedAt);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetDoctorReportByFindingId
    @FindingId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, FindingId, Verdict, ErrorCategory, RootCauseHypothesis, Confidence, CitationsJson, ProposedFixJson, ModelUsed, TokensIn, TokensOut, GeneratedAt, HumanFeedback
    FROM monitoring.DoctorReport
    WHERE FindingId = @FindingId
    ORDER BY GeneratedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllDoctorReports
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, FindingId, Verdict, ErrorCategory, RootCauseHypothesis, Confidence, CitationsJson, ProposedFixJson, ModelUsed, TokensIn, TokensOut, GeneratedAt, HumanFeedback
    FROM monitoring.DoctorReport
    ORDER BY GeneratedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_RecordMonkeyRun
    @MonkeyKey nvarchar(64), @LastRunAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    MERGE monitoring.MonkeyRun WITH (HOLDLOCK) AS target
    USING (SELECT @MonkeyKey AS MonkeyKey) AS incoming
        ON target.MonkeyKey = incoming.MonkeyKey
    WHEN MATCHED THEN
        UPDATE SET LastRunAt = @LastRunAt
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MonkeyKey, LastRunAt) VALUES (@MonkeyKey, @LastRunAt);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetMonkeyLastRun
    @MonkeyKey nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT LastRunAt FROM monitoring.MonkeyRun WHERE MonkeyKey = @MonkeyKey;
END
GO

-- @ProfileKey defaults to N'' - the SHARED corpus - because that is the pre-multi-company meaning
-- of every existing caller, and a chunk nobody scoped is readable by everyone by definition. A
-- company-private document has to say so explicitly.
CREATE OR ALTER PROCEDURE monitoring.usp_AddKnowledgeChunk
    @Id uniqueidentifier, @SourceType nvarchar(64), @SourceRef nvarchar(400) = NULL,
    @Content nvarchar(max), @EmbeddingJson nvarchar(max), @EmbeddingModel nvarchar(128) = NULL,
    @Domain nvarchar(64) = NULL, @DocumentTitle nvarchar(400) = NULL, @SectionTitle nvarchar(200) = NULL,
    @ContentHash char(64) = NULL, @ProfileKey nvarchar(128) = N'', @CreatedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.KnowledgeChunk
        (Id, SourceType, SourceRef, Content, EmbeddingJson, EmbeddingModel, Domain, DocumentTitle, SectionTitle, ContentHash, ProfileKey, CreatedAt)
    VALUES
        (@Id, @SourceType, @SourceRef, @Content, @EmbeddingJson, @EmbeddingModel, @Domain, @DocumentTitle, @SectionTitle, @ContentHash, ISNULL(@ProfileKey, N''), @CreatedAt);
END
GO

-- Read once at the start of an import so a re-run skips what is already stored. Returns only the
-- hashes, not the chunks: the corpus is thousands of rows and the import needs nothing else.
CREATE OR ALTER PROCEDURE monitoring.usp_GetKnowledgeChunkHashes
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ContentHash FROM monitoring.KnowledgeChunk WHERE ContentHash IS NOT NULL;
END
GO

-- The isolation boundary for retrieval. @ProfileKey NULL means the whole corpus (the callers that
-- are not per-company, e.g. the portal's own knowledge search); a concrete key returns that
-- company's chunks PLUS the shared ones, and nothing else.
--
-- ProfileKey is deliberately NOT in the SELECT list: SqlKnowledgeRetriever.KnowledgeChunkRow is a
-- positional record that Dapper matches by position, name and exact count, and the filter is
-- already enforced here.
CREATE OR ALTER PROCEDURE monitoring.usp_GetAllKnowledgeChunks
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, SourceType, SourceRef, Content, EmbeddingJson, EmbeddingModel,
           Domain, DocumentTitle, SectionTitle, ContentHash, CreatedAt
    FROM monitoring.KnowledgeChunk
    WHERE (@ProfileKey IS NULL OR ProfileKey = @ProfileKey OR ProfileKey = N'')
    ORDER BY CreatedAt DESC;
END
GO

-- Inventory for the portal: one row per document, not per chunk. Deliberately never selects
-- Content, so listing the corpus stays cheap no matter how large it grows.
--
-- Both "no vector" sentinels come from the caller rather than being hardcoded here, so they stay
-- defined in exactly one place (KnowledgeChunk). Counting only one of them would report an
-- unembedded chunk as retrievable by meaning, which is the opposite of what this column is for.
CREATE OR ALTER PROCEDURE monitoring.usp_GetKnowledgeDocuments
    @UnknownEmbeddingModel nvarchar(128),
    @NoEmbeddingModel nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT  DocumentTitle       = ISNULL(DocumentTitle, N'(untitled)'),
            SourceType          = SourceType,
            Domain              = ISNULL(MAX(Domain), N''),
            Chunks              = COUNT(*),
            ChunksWithEmbedding = SUM(CASE WHEN EmbeddingModel IS NULL
                                             OR EmbeddingModel = @UnknownEmbeddingModel
                                             OR EmbeddingModel = @NoEmbeddingModel THEN 0 ELSE 1 END),
            IndexedAt           = MAX(CreatedAt)
    FROM monitoring.KnowledgeChunk
    GROUP BY ISNULL(DocumentTitle, N'(untitled)'), SourceType
    ORDER BY MAX(CreatedAt) DESC;
END
GO

-- One document's indexed text, in the order it was chunked. Never returns EmbeddingJson: a vector
-- is thousands of numbers that no reader can use, and shipping it would dwarf the text itself.
CREATE OR ALTER PROCEDURE monitoring.usp_GetKnowledgeChunksByDocument
    @DocumentTitle nvarchar(400),
    @SourceType nvarchar(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT  SectionTitle = ISNULL(SectionTitle, N''),
            Content,
            SourceRef    = ISNULL(SourceRef, N'')
    FROM monitoring.KnowledgeChunk
    WHERE ISNULL(DocumentTitle, N'(untitled)') = @DocumentTitle
      AND (@SourceType IS NULL OR SourceType = @SourceType)
    ORDER BY CreatedAt, Id;
END
GO
