-- What a company IS, versioned. Grain: one row per ProfileKey in monitoring.CompanyProfile, with
-- CompanyProfileAudit as append-only history - same shape as monitoring.KqlQuery(+Audit).
--
-- Why this table exists: which services exist, which steps a request can belong to, how to tell
-- who is affected and what to call all of it are currently spelled out in code and baked into the
-- KQL. Onboarding a second company therefore means editing C# and redeploying. A profile is a
-- document, so it is persisted as ONE nvarchar(max) JSON column rather than column-per-property -
-- the shape is fixed by JourneyMonitor.Core.Company.CompanyProfile, and nesting services ->
-- flows -> steps -> matchers into relational tables would buy nothing but five more joins.
--
-- No CHECK on ProfileKey, on purpose, for the same reason monitoring.AgentPauseSetting has none:
-- a new company must be a row, not a schema migration. That is the entire point of the table.
--
-- Why the audit carries the WHOLE ProfileJson and not just "the profile changed": this document
-- decided how every telemetry row was classified, which club/tenant was blamed and which team was
-- paged. Restoring a previous revision has to be a COPY, not a reconstruction, and a reader
-- investigating an old alert has to be able to see exactly what mapping was in effect at the
-- time. Same call as monitoring.KqlQueryAudit, and the opposite of
-- monitoring.LlmClientSettingAudit, where the value is a secret and must NOT be kept.
--
-- Anything read out of here reaches an unattended prompt (CompanyGlossary), so every consumer
-- must pass it through PromptSanitizer: a profile is edited by whoever onboards a customer, which
-- is the same trust level as a wiki page.
--
-- Serialisation: the C# side writes this document with JsonSerializerDefaults.Web, so the
-- property names in the seeds below are camelCase. PascalCase here would deserialise to a profile
-- whose every field is null, silently.
--
-- Idempotency: this script is applied on every container start by docker/init-sql.sh and may be
-- applied by hand to an already-provisioned database. Every statement is therefore guarded - the
-- CREATE TABLE by OBJECT_ID, every column added after the first revision by its own COL_LENGTH
-- check (a CREATE TABLE guard alone never reaches a table that already exists), the index by
-- sys.indexes, the procedures by CREATE OR ALTER, and the seeds by IF NOT EXISTS so re-running
-- never overwrites a profile an operator edited in the portal.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.CompanyProfile', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.CompanyProfile
    (
        -- Stable identity used by the code and by the admin route, e.g. 'arrivia', 'demo'.
        -- No CHECK: see the header.
        ProfileKey   nvarchar(128)     NOT NULL
            CONSTRAINT PK_monitoring_CompanyProfile PRIMARY KEY,
        -- The whole CompanyProfile document, camelCase (JsonSerializerDefaults.Web).
        -- Default N'{}' so the ALTER below can add it NOT NULL to an older table.
        ProfileJson  nvarchar(max)     NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfile_ProfileJson DEFAULT N'{}',
        -- Incremented by the upsert, and written back into ProfileJson by it so the stored
        -- document and the column can never disagree. Lets the UI show "v7" and the audit
        -- identify what to restore.
        Version      int               NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfile_Version DEFAULT 1,
        -- Duplicated out of the document so the list view does not have to parse 20 JSON blobs.
        DisplayName  nvarchar(256)     NULL,
        Enabled      bit               NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfile_Enabled DEFAULT 1,
        Notes        nvarchar(1000)    NULL,
        UpdatedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfile_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfile_UpdatedBy DEFAULT N''
    );
END
GO

-- Per-column guards. The CREATE TABLE above does nothing on a database that already has an
-- earlier revision of this table, so every non-key column needs its own ALTER path or it never
-- arrives. ProfileKey is deliberately absent from this list: it is the primary key, and a table
-- without it is not an earlier revision of this table - ALTER TABLE ... ADD could not create it
-- as the PK anyway.
IF COL_LENGTH(N'monitoring.CompanyProfile', N'ProfileJson') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD ProfileJson nvarchar(max) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfile_ProfileJson DEFAULT N'{}';
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'Version') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD Version int NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfile_Version DEFAULT 1;
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'DisplayName') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD DisplayName nvarchar(256) NULL;
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'Enabled') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD Enabled bit NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfile_Enabled DEFAULT 1;
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'Notes') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD Notes nvarchar(1000) NULL;
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'UpdatedAt') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD UpdatedAt datetimeoffset(7) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfile_UpdatedAt DEFAULT SYSDATETIMEOFFSET();
GO
IF COL_LENGTH(N'monitoring.CompanyProfile', N'UpdatedBy') IS NULL
    ALTER TABLE monitoring.CompanyProfile ADD UpdatedBy nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfile_UpdatedBy DEFAULT N'';
GO

IF OBJECT_ID(N'monitoring.CompanyProfileAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.CompanyProfileAudit
    (
        AuditId      bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_CompanyProfileAudit PRIMARY KEY,
        ProfileKey   nvarchar(128)     NOT NULL,
        Version      int               NOT NULL,
        -- The whole document, on purpose. See the header: this is what makes rollback a copy.
        -- NOT NULL even on a delete - the last known document is written instead, so removing a
        -- profile does not destroy the ability to restore it, and so the reader never has to
        -- model a null document.
        ProfileJson  nvarchar(max)     NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfileAudit_ProfileJson DEFAULT N'{}',
        -- Set when the row was removed, so "this company was off-boarded" is a recorded event
        -- rather than a gap in the history.
        Removed      bit               NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfileAudit_Removed DEFAULT 0,
        ChangedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfileAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_CompanyProfileAudit_ChangedBy DEFAULT N''
    );
END
GO

IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_ProfileKey DEFAULT N'';
GO
IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'Version') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD Version int NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_Version DEFAULT 0;
GO
IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'ProfileJson') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD ProfileJson nvarchar(max) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_ProfileJson DEFAULT N'{}';
GO
IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'Removed') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD Removed bit NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_Removed DEFAULT 0;
GO
IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'ChangedAt') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD ChangedAt datetimeoffset(7) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET();
GO
IF COL_LENGTH(N'monitoring.CompanyProfileAudit', N'ChangedBy') IS NULL
    ALTER TABLE monitoring.CompanyProfileAudit ADD ChangedBy nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_CompanyProfileAudit_ChangedBy DEFAULT N'';
GO

-- Guarded separately rather than inside the CREATE TABLE block: an index created only there never
-- reaches a database whose audit table was provisioned by an earlier revision of this script.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_monitoring_CompanyProfileAudit_Read'
      AND object_id = OBJECT_ID(N'monitoring.CompanyProfileAudit', N'U'))
    CREATE INDEX IX_monitoring_CompanyProfileAudit_Read
        ON monitoring.CompanyProfileAudit (ProfileKey, ChangedAt DESC);
GO

-- Enabled = 1 is enforced here, not in the caller: ICompanyProfileStore.GetAsync is documented to
-- return null for a disabled row, and a profile that is switched off must never classify traffic.
-- The admin UI reads disabled rows through usp_GetCompanyProfiles, which does not filter.
CREATE OR ALTER PROCEDURE monitoring.usp_GetCompanyProfile
    @ProfileKey nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.CompanyProfile
    WHERE ProfileKey = @ProfileKey AND Enabled = 1;
END
GO

-- Documents included: there are a handful of companies, not thousands, and every caller of
-- GetAllAsync wants the mapping and not just the names.
CREATE OR ALTER PROCEDURE monitoring.usp_GetCompanyProfiles
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.CompanyProfile
    ORDER BY ProfileKey;
END
GO

-- MERGE WITH (HOLDLOCK) rather than IF NOT EXISTS + UPDATE: two concurrent writers on the same
-- ProfileKey would otherwise race between the check and the insert, and the version would skip -
-- the same race that was fixed in usp_UpsertLlmClientSetting.
--
-- JSON_MODIFY writes the bumped version back into the document so the stored/audited JSON is
-- self-consistent with the Version column. Restoring revision 7 has to produce a document that
-- says 7; one that still says 6 would be a lie in the one place this table exists to be honest.
-- JSON_MODIFY also rejects a malformed document, which is the right place to refuse it.
CREATE OR ALTER PROCEDURE monitoring.usp_UpsertCompanyProfile
    @ProfileKey nvarchar(128), @ProfileJson nvarchar(max), @DisplayName nvarchar(256),
    @Enabled bit, @Notes nvarchar(1000), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NextVersion int =
        ISNULL((SELECT Version + 1 FROM monitoring.CompanyProfile WHERE ProfileKey = @ProfileKey), 1);

    DECLARE @StoredJson nvarchar(max) = JSON_MODIFY(@ProfileJson, N'$.version', @NextVersion);

    MERGE monitoring.CompanyProfile WITH (HOLDLOCK) AS target
    USING (SELECT @ProfileKey AS ProfileKey) AS source
        ON target.ProfileKey = source.ProfileKey
    WHEN MATCHED THEN
        UPDATE SET ProfileJson = @StoredJson, Version = @NextVersion, DisplayName = @DisplayName,
                   Enabled = @Enabled, Notes = @Notes,
                   UpdatedAt = SYSDATETIMEOFFSET(), UpdatedBy = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedAt, UpdatedBy)
        VALUES (@ProfileKey, @StoredJson, @NextVersion, @DisplayName, @Enabled, @Notes,
                SYSDATETIMEOFFSET(), @ChangedBy);

    INSERT INTO monitoring.CompanyProfileAudit (ProfileKey, Version, ProfileJson, Removed, ChangedBy)
    VALUES (@ProfileKey, @NextVersion, @StoredJson, 0, @ChangedBy);

    SELECT ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedAt, UpdatedBy
    FROM monitoring.CompanyProfile
    WHERE ProfileKey = @ProfileKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_DeleteCompanyProfile
    @ProfileKey nvarchar(128), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Version int = 0, @LastJson nvarchar(max) = N'{}';
    SELECT @Version = Version, @LastJson = ProfileJson
    FROM monitoring.CompanyProfile
    WHERE ProfileKey = @ProfileKey;

    DELETE FROM monitoring.CompanyProfile WHERE ProfileKey = @ProfileKey;

    -- The last known document is kept, not nulled: off-boarding a company must not destroy the
    -- mapping that explains every alert already raised under it. Recorded even when nothing was
    -- deleted - "someone removed this company" is history either way.
    INSERT INTO monitoring.CompanyProfileAudit (ProfileKey, Version, ProfileJson, Removed, ChangedBy)
    VALUES (@ProfileKey, @Version, @LastJson, 1, @ChangedBy);
END
GO

-- Documents included, unlike usp_GetKqlQueryAudit: ICompanyProfileStore.GetAuditAsync hands back
-- CompanyProfileAuditEntry, which carries ProfileJson, so a second per-entry round trip would buy
-- nothing. Callers cap @Top.
CREATE OR ALTER PROCEDURE monitoring.usp_GetCompanyProfileAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) AuditId, ProfileKey, Version, ProfileJson, Removed, ChangedAt, ChangedBy
    FROM monitoring.CompanyProfileAudit
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO

-- Seeds. IF NOT EXISTS with (UPDLOCK, HOLDLOCK) rather than a bare existence check: init-sql.sh
-- applies this to two databases and can run concurrently with a portal write, and the key range
-- lock is what makes "insert only if absent" actually atomic. INSERT-only on purpose - a
-- WHEN MATCHED branch would overwrite an operator's edited profile on every container start.

-- Arrivia, the pilot customer, written down as a ROW. Derived from ONE source of truth: the
-- Club=coalesce(...) identity chain and the four per-vertical StageNo/Stage case() blocks of
-- Infrastructure/Kql/scout-latency-priorities.kql.txt. Nothing here comes from the journey
-- resolver or the journey SQL schemas.
--
-- HOW "order" WORKS HERE. ProfileDrivenAdapter flattens every step of every service, sorts by
-- order and takes the FIRST match - there is no per-vertical partition the way the KQL has one.
-- So "order" carries two jobs at once: the KQL vertical precedence (Cruise -> Cars -> Hotels ->
-- Shared) becomes the global bands 10 / 30 / 40 / 90, and the evaluation order INSIDE each case()
-- block is preserved inside its band. That is why "03 Hotel Results" (40) sorts before "02 Hotel
-- Search" (41): BackgroundHotelSearch.json literally contains "search.json", so the more specific
-- rule has to be tested first, exactly as the KQL does it. The leading number in each displayName
-- is the funnel position; "order" is the evaluation sequence, and where they disagree the
-- displayName is the one a human should read.
--
-- DEVIATIONS from the legacy query, all deliberate (see the track report for the reasoning):
--   * endswith -> url-contains. The clean matcher set has no suffix kind. Consequence:
--     "/cruisecabinonhold" now CONTAINS "/cruisecabin", which the endswith version did not, so
--     06 Cabin Selection is ordered BEFORE 05 Total Price to keep the KQL outcome. Same reason
--     "/cruisepaymentproc" needs no rule of its own - "/cruisepayment" already covers it.
--   * The three broadest Cruise markers ("payment", "confirm", "booking") are DROPPED. They were
--     only safe because the KQL had already partitioned by telemetry source; without that
--     partition "booking" would claim every CarBooking url and "payment" every checkout.
--   * The "Shared / unclassified" pseudo-vertical becomes a REAL service (shared) holding the
--     five endpoints that are genuinely the same whatever the member is buying. The legacy
--     repeats those rules verbatim in the Hotels, Cars and Shared branches; they are declared
--     once here. "Unmapped operation" is not modelled: unmatched traffic resolves to
--     CompanyResolution.Unresolved, which is the honest name for a mapping gap.
--   * "/reservations/cancel" moves from Cars to shared. The legacy gives Cars a 09 Cancellation
--     rule and forgets Hotels; the endpoint is the same one.
--   * Air is absent. scout-latency-priorities.kql.txt has no Air vertical at all, and this seed
--     describes that query and nothing else.
--   * No hostFilters anywhere. Arrivia is white-label: every club has its own hostname, so
--     pinning a service to a host would make it miss for every club but one. The legacy does not
--     filter by host either.
-- NOT EXPRESSIBLE by the model, and therefore simply absent:
--   * VerticalHint (customDimensions.Verticals / .Vertical). There is no custom-dimension step
--     matcher, so a checkout url that the KQL routes to Hotels purely because the row is stamped
--     Vertical="Hotel" lands on the shared service here.
--   * Source/cloud_RoleName. Cruise is selected in the KQL by which Application Insights app the
--     row came from; here it is recognised only by its url markers.
--
-- WHO IS AFFECTED: why the chain ends in a host, and why CobrandId is gone.
-- Measured on the live Hotels/SOR resource (app 8644ddf0-29b2-4283-87c0-7a9a514aefb5, 3h window):
-- 158,376 requests and 25,410 exceptions, of which 100% / 87% carry a url, while the stamped club
-- dimensions cover almost nothing - ClubId 852 rows, club_id 342, VacationClubId 6, and CobrandId
-- ZERO. A chain made only of those dimensions resolves nothing for ~95% of the estate, which is
-- what every package and every forensic case reported before this line existed: ClubId NULL,
-- EntityKey "[host/club unavailable]", and the Inspector refusing to run club_baseline,
-- signature_breakdown and client_breakdown because there was no entity to scope them to.
--   * The host source is LAST, so an explicitly stamped club id always outranks it. A dimension
--     the application wrote is evidence; a hostname is a fallback.
--   * entityKind is "host", NOT "club". This is the whole reason the fallback is honest: the key
--     becomes "host:members.example.com", ClubIdentityResolver recognises that shape and maps it
--     to a club through brand.ClubInventory.Site, and it reports HOW (host-exact / host-base-domain
--     rather than club-id). Nothing claims the hostname IS the club; the catalogue makes that step
--     and says so. Declaring entityKind "club" here would produce "club:members.example.com",
--     which the resolver would try to match against ClubId/IcePartnerId and never resolve.
--   * CobrandId is REMOVED. Zero occurrences in 23,802 sampled rows - the dimension is not emitted
--     at all, unlike ClubId and club_id which are rare but real. The cobrand concept still reaches
--     the platform as the two query parameters below, which is why they stay. A source that can
--     never match costs an arm in the rendered coalesce() of every board query and a step in every
--     future "which source answered?" investigation. If a resource outside SOR turns out to stamp
--     it, re-adding it is one profile edit through the admin API, not a deployment.
IF NOT EXISTS (SELECT 1 FROM monitoring.CompanyProfile WITH (UPDLOCK, HOLDLOCK) WHERE ProfileKey = N'arrivia')
BEGIN
    INSERT INTO monitoring.CompanyProfile (ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedBy)
    VALUES (N'arrivia', N'{
  "profileKey": "arrivia",
  "displayName": "Arrivia",
  "labels": { "service": "Vertical", "flow": "Journey", "step": "Stage", "entity": "Club", "owner": "Owning team" },
  "services": [
    {
      "key": "cruises",
      "displayName": "Cruises",
      "telemetrySourceAliases": ["cruise-core", "cruise-api"],
      "hostFilters": [],
      "flows": [
        {
          "key": "booking",
          "displayName": "Cruise booking",
          "steps": [
            { "key": "02-cruise-listing", "displayName": "02 Cruise Listing", "order": 10, "matchers": [ { "kind": "path-contains", "value": "/cruisesearch", "caseSensitive": false }, { "kind": "path-contains", "value": "/getcustomerid", "caseSensitive": false }, { "kind": "path-contains", "value": "/searchmatrix", "caseSensitive": false } ] },
            { "key": "03-cruise-detail", "displayName": "03 Cruise Detail", "order": 11, "matchers": [ { "kind": "path-contains", "value": "/cruiseitinerary", "caseSensitive": false } ] },
            { "key": "04-rooms-and-rates", "displayName": "04 Rooms & Rates", "order": 12, "matchers": [ { "kind": "path-contains", "value": "/cruisecategory", "caseSensitive": false } ] },
            { "key": "06-cabin-selection", "displayName": "06 Cabin Selection", "order": 13, "matchers": [ { "kind": "path-contains", "value": "/cruisecabinonhold", "caseSensitive": false } ] },
            { "key": "05-total-price", "displayName": "05 Total Price", "order": 14, "matchers": [ { "kind": "path-contains", "value": "/cruisecabin", "caseSensitive": false } ] },
            { "key": "07-passenger", "displayName": "07 Passenger", "order": 15, "matchers": [ { "kind": "path-contains", "value": "passenger", "caseSensitive": false }, { "kind": "path-contains", "value": "/secure/cruisetravelwith.jsp", "caseSensitive": false }, { "kind": "path-contains", "value": "/api/guest/get-guests", "caseSensitive": false } ] },
            { "key": "08-options", "displayName": "08 Options", "order": 16, "matchers": [ { "kind": "path-contains", "value": "/featuresandoptions", "caseSensitive": false }, { "kind": "path-contains", "value": "/insurance", "caseSensitive": false } ] },
            { "key": "09-payment", "displayName": "09 Payment", "order": 17, "matchers": [ { "kind": "path-contains", "value": "/cruisepayment", "caseSensitive": false } ] },
            { "key": "10-review", "displayName": "10 Review", "order": 18, "matchers": [ { "kind": "path-contains", "value": "/secure/cruisesummary.jsp", "caseSensitive": false } ] },
            { "key": "11-confirmation", "displayName": "11 Confirmation", "order": 19, "matchers": [ { "kind": "path-contains", "value": "/cruiseconfirm", "caseSensitive": false } ] }
          ]
        }
      ],
      "fallbackStep": null
    },
    {
      "key": "cars",
      "displayName": "Cars",
      "telemetrySourceAliases": ["sor-frontend"],
      "hostFilters": [],
      "flows": [
        {
          "key": "booking",
          "displayName": "Car booking",
          "steps": [
            { "key": "03-car-results", "displayName": "03 Car Results", "order": 30, "matchers": [ { "kind": "path-contains", "value": "backgroundcarsearch", "caseSensitive": false }, { "kind": "path-contains", "value": "/cars/", "caseSensitive": false } ] },
            { "key": "02-car-search", "displayName": "02 Car Search", "order": 31, "matchers": [ { "kind": "path-contains", "value": "carlocationsearch", "caseSensitive": false }, { "kind": "path-contains", "value": "searchlocations", "caseSensitive": false }, { "kind": "path-contains", "value": "carsearch", "caseSensitive": false } ] },
            { "key": "05-prebook", "displayName": "05 PreBook", "order": 32, "matchers": [ { "kind": "path-contains", "value": "carbooking", "caseSensitive": false } ] }
          ]
        }
      ],
      "fallbackStep": null
    },
    {
      "key": "hotels",
      "displayName": "Hotels",
      "telemetrySourceAliases": ["sor-frontend"],
      "hostFilters": [],
      "flows": [
        {
          "key": "booking",
          "displayName": "Hotel booking",
          "steps": [
            { "key": "03-hotel-results", "displayName": "03 Hotel Results", "order": 40, "matchers": [ { "kind": "path-contains", "value": "backgroundhotelsearch", "caseSensitive": false } ] },
            { "key": "02-hotel-search", "displayName": "02 Hotel Search", "order": 41, "matchers": [ { "kind": "path-contains", "value": "search.json", "caseSensitive": false } ] },
            { "key": "04-hotel-details", "displayName": "04 Hotel Details", "order": 42, "matchers": [ { "kind": "path-contains", "value": "searchrooms", "caseSensitive": false }, { "kind": "path-contains", "value": "getcancelpolicy", "caseSensitive": false }, { "kind": "path-contains", "value": "hoteldetails", "caseSensitive": false }, { "kind": "path-contains", "value": "zentrumhubhoteldetailscontent", "caseSensitive": false }, { "kind": "path-contains", "value": "getreviews", "caseSensitive": false } ] },
            { "key": "05-prebook", "displayName": "05 PreBook", "order": 43, "matchers": [ { "kind": "path-contains", "value": "book.json", "caseSensitive": false } ] }
          ]
        }
      ],
      "fallbackStep": null
    },
    {
      "key": "shared",
      "displayName": "Shared Booking Services",
      "telemetrySourceAliases": ["sor-frontend"],
      "hostFilters": [],
      "flows": [
        {
          "key": "booking",
          "displayName": "Shared booking steps",
          "steps": [
            { "key": "01-login", "displayName": "01 Login", "order": 1, "matchers": [ { "kind": "path-contains", "value": "login.json", "caseSensitive": false }, { "kind": "path-contains", "value": "logincheck", "caseSensitive": false } ] },
            { "key": "08-confirmation", "displayName": "08 Confirmation", "order": 90, "matchers": [ { "kind": "path-contains", "value": "purchasecomplete", "caseSensitive": false } ] },
            { "key": "07-booking", "displayName": "07 Booking", "order": 91, "matchers": [ { "kind": "path-contains", "value": "completepurchase", "caseSensitive": false }, { "kind": "path-contains", "value": "determine3dseligibility", "caseSensitive": false } ] },
            { "key": "06-checkout", "displayName": "06 Checkout", "order": 92, "matchers": [ { "kind": "path-contains", "value": "/reservations/purchase", "caseSensitive": false }, { "kind": "path-contains", "value": "userpeople_read", "caseSensitive": false }, { "kind": "path-contains", "value": "addremoveinsurance", "caseSensitive": false }, { "kind": "path-contains", "value": "travelerprofile", "caseSensitive": false } ] },
            { "key": "09-cancellation", "displayName": "09 Cancellation", "order": 93, "matchers": [ { "kind": "path-contains", "value": "/reservations/cancel", "caseSensitive": false } ] }
          ]
        }
      ],
      "fallbackStep": null
    }
  ],
  "entityIdentitySources": [
    { "order": 1, "kind": "custom-dimension", "key": "VacationClubId", "entityKind": "club" },
    { "order": 2, "kind": "custom-dimension", "key": "ClubId", "entityKind": "club" },
    { "order": 3, "kind": "custom-dimension", "key": "club_id", "entityKind": "club" },
    { "order": 4, "kind": "query-parameter", "key": "cobrand", "entityKind": "club" },
    { "order": 5, "kind": "query-parameter", "key": "cobrandId", "entityKind": "club" },
    { "order": 6, "kind": "host", "key": "", "entityKind": "host" }
  ],
  "glossary": {
    "serviceNoun": "vertical",
    "flowNoun": "journey",
    "stepNoun": "stage",
    "entityNoun": "club",
    "ownerNoun": "owning team",
    "description": "Arrivia runs white-label travel booking sites for member clubs. A member arrives on a club-branded site and moves through a booking funnel - search, results, detail, pre-book, checkout, booking, confirmation - in one of three verticals: Hotels, Cars and Cruises. Hotels and Cars share one storefront; Cruises runs on its own core and API. Login, checkout, booking, confirmation and cancellation hit the same endpoints whatever the member is buying, so they are grouped as Shared Booking Services. Who is affected is always a club. Most traffic does not stamp a club id, so it is identified by the club-branded hostname it was served on, which the club inventory maps to the club; a stamped club id, when present, is the stronger answer and wins."
  },
  "version": 1,
  "enabled": true,
  "notes": "Derived from the scout board KQL''s Club=coalesce(...) chain and its four StageNo/Stage case() blocks. The deviations from that query are listed in the comment above this seed; they are corrections, not drift."
}', 1, N'Arrivia', 1,
    N'Pilot customer. Derived from scout-latency-priorities.kql.txt; see the comment above the seed for every deliberate deviation.', N'seed');
END
GO

-- Migration for databases seeded BEFORE the host identity source existed. The seed above is
-- INSERT-only by design, so on every database that already has an arrivia row it does nothing -
-- which would leave the defect this change fixes live everywhere except a fresh container.
--
-- Scoped so it can only ever touch the document the seed itself wrote: it requires the chain to
-- still be the original six entries ending in CobrandId/cobrand/cobrandId, and UpdatedBy to still
-- start with "seed". A profile an operator edited in the portal is left alone and reported by the
-- Scout as it stands - silently rewriting someone else's mapping is the data loss the seed guard
-- exists to prevent, and it does not become acceptable just because this edit is a fix.
--
-- Idempotent by shape, not by a flag: once the chain carries the host source the predicate below
-- no longer matches, so re-applying the script is a no-op. JSON_MODIFY also refuses a malformed
-- document, which is the right place to stop.
IF EXISTS (
    SELECT 1 FROM monitoring.CompanyProfile WITH (UPDLOCK, HOLDLOCK)
    WHERE ProfileKey = N'arrivia'
      AND UpdatedBy LIKE N'seed%'
      AND JSON_VALUE(ProfileJson, N'$.entityIdentitySources[3].key') = N'CobrandId'
      AND JSON_VALUE(ProfileJson, N'$.entityIdentitySources[5].key') = N'cobrandId'
      AND JSON_QUERY(ProfileJson, N'$.entityIdentitySources[6]') IS NULL)
BEGIN
    DECLARE @ArriviaChain nvarchar(max) = N'[
    { "order": 1, "kind": "custom-dimension", "key": "VacationClubId", "entityKind": "club" },
    { "order": 2, "kind": "custom-dimension", "key": "ClubId", "entityKind": "club" },
    { "order": 3, "kind": "custom-dimension", "key": "club_id", "entityKind": "club" },
    { "order": 4, "kind": "query-parameter", "key": "cobrand", "entityKind": "club" },
    { "order": 5, "kind": "query-parameter", "key": "cobrandId", "entityKind": "club" },
    { "order": 6, "kind": "host", "key": "", "entityKind": "host" }
  ]';
    DECLARE @ArriviaDescription nvarchar(max) = N'Arrivia runs white-label travel booking sites for member clubs. A member arrives on a club-branded site and moves through a booking funnel - search, results, detail, pre-book, checkout, booking, confirmation - in one of three verticals: Hotels, Cars and Cruises. Hotels and Cars share one storefront; Cruises runs on its own core and API. Login, checkout, booking, confirmation and cancellation hit the same endpoints whatever the member is buying, so they are grouped as Shared Booking Services. Who is affected is always a club. Most traffic does not stamp a club id, so it is identified by the club-branded hostname it was served on, which the club inventory maps to the club; a stamped club id, when present, is the stronger answer and wins.';

    DECLARE @ArriviaVersion int = ISNULL((SELECT Version FROM monitoring.CompanyProfile WHERE ProfileKey = N'arrivia'), 1) + 1;
    DECLARE @ArriviaJson nvarchar(max);

    SELECT @ArriviaJson = JSON_MODIFY(
               JSON_MODIFY(
                   JSON_MODIFY(ProfileJson, N'$.entityIdentitySources', JSON_QUERY(@ArriviaChain)),
                   N'$.glossary.description', @ArriviaDescription),
               N'$.version', @ArriviaVersion)
    FROM monitoring.CompanyProfile
    WHERE ProfileKey = N'arrivia';

    UPDATE monitoring.CompanyProfile
    SET ProfileJson = @ArriviaJson,
        Version = @ArriviaVersion,
        UpdatedAt = SYSDATETIMEOFFSET(),
        UpdatedBy = N'seed:host-identity'
    WHERE ProfileKey = N'arrivia';

    -- Audited like any other profile write: "why did this club start resolving on the 3rd" has to
    -- be answerable from the table that answers it for portal edits, not from a deployment log.
    INSERT INTO monitoring.CompanyProfileAudit (ProfileKey, Version, ProfileJson, Removed, ChangedBy)
    VALUES (N'arrivia', @ArriviaVersion, @ArriviaJson, 0, N'seed:host-identity');
END
GO

-- Demo: the same telemetry described with a DIFFERENT vocabulary, which is the whole proof that
-- the platform is decoupled. One service called "checkout" instead of a vertical, four steps named
-- step-01..step-04 instead of booking stages, and the entity is a "tenant" rather than a club -
-- while the identity sources still point at the very same custom dimensions the Arrivia traffic
-- stamps. If anything in the product still reads "Vertical"/"Stage"/"Club" after the refactor,
-- rendering this profile is what will show it.
IF NOT EXISTS (SELECT 1 FROM monitoring.CompanyProfile WITH (UPDLOCK, HOLDLOCK) WHERE ProfileKey = N'demo')
BEGIN
    INSERT INTO monitoring.CompanyProfile (ProfileKey, ProfileJson, Version, DisplayName, Enabled, Notes, UpdatedBy)
    VALUES (N'demo', N'{
  "profileKey": "demo",
  "displayName": "Demo Company",
  "labels": { "service": "Product area", "flow": "Journey", "step": "Step", "entity": "Tenant", "owner": "Owning team" },
  "services": [
    {
      "key": "checkout",
      "displayName": "Checkout",
      "telemetrySourceAliases": ["club"],
      "hostFilters": [],
      "flows": [
        {
          "key": "purchase",
          "displayName": "Purchase",
          "steps": [
            { "key": "step-01", "displayName": "Step 01", "order": 1, "matchers": [ { "kind": "path-contains", "value": "/search", "caseSensitive": false } ] },
            { "key": "step-02", "displayName": "Step 02", "order": 2, "matchers": [ { "kind": "path-contains", "value": "/details", "caseSensitive": false } ] },
            { "key": "step-03", "displayName": "Step 03", "order": 3, "matchers": [ { "kind": "path-contains", "value": "/checkout", "caseSensitive": false } ] },
            { "key": "step-04", "displayName": "Step 04", "order": 4, "matchers": [ { "kind": "path-contains", "value": "/confirmation", "caseSensitive": false } ] }
          ]
        }
      ],
      "fallbackStep": null
    }
  ],
  "entityIdentitySources": [
    { "order": 1, "kind": "custom-dimension", "key": "VacationClubId", "entityKind": "tenant" },
    { "order": 2, "kind": "custom-dimension", "key": "IcePartnerId", "entityKind": "tenant" },
    { "order": 3, "kind": "query-parameter", "key": "clubId", "entityKind": "tenant" },
    { "order": 4, "kind": "host", "key": "", "entityKind": "tenant" }
  ],
  "glossary": {
    "serviceNoun": "product area",
    "flowNoun": "journey",
    "stepNoun": "step",
    "entityNoun": "tenant",
    "ownerNoun": "owning team",
    "description": "A sample company whose product is a four-step online checkout. Exists to prove the platform carries no Arrivia vocabulary of its own."
  },
  "version": 1,
  "enabled": true,
  "notes": "Decoupling probe. Same telemetry, different ontology."
}', 1, N'Demo Company', 1,
    N'Decoupling probe: deliberately different vocabulary over the same telemetry.', N'seed');
END
GO

-- WHICH company is being monitored right now. Grain: exactly one row, ever.
--
-- Why this is a table and not configuration: Company:DefaultProfileKey is bound from
-- IConfiguration at startup, which in a container means an environment variable, which means
-- switching customer is a redeploy. The profiles themselves are already rows; the pointer at them
-- has to be one too, or onboarding a second company still ends in a restart.
--
-- No audit table, on purpose. monitoring.CompanyProfileAudit exists to answer "what did this
-- mapping SAY when that alert was raised" - it is about the CONTENT of a profile, and that is why
-- it carries the whole document. WHICH profile is selected is a one-field preference;
-- UpdatedAt/UpdatedBy already answer "who switched us last", and a second append-only table would
-- have to be read, trimmed and restored for a single nvarchar.
--
-- No seed row either: an empty table means "nobody has chosen", which is exactly the state in
-- which Company:DefaultProfileKey should stay in charge. Seeding 'arrivia' here would silently
-- override whatever a deployment had configured, on the next container start.
IF OBJECT_ID(N'monitoring.ActiveCompanyProfile', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ActiveCompanyProfile
    (
        -- Singleton guard: the PK makes a second row impossible and the CHECK makes the only
        -- legal key 1, so "the active company" cannot become a set that something has to pick
        -- from. Cheaper and more honest than a trigger, and it survives writes made in SSMS.
        LockId     tinyint           NOT NULL
            CONSTRAINT PK_monitoring_ActiveCompanyProfile PRIMARY KEY
            CONSTRAINT CK_monitoring_ActiveCompanyProfile_SingleRow CHECK (LockId = 1),
        -- Deliberately NOT a foreign key to monitoring.CompanyProfile: off-boarding a company
        -- would then fail on this row, or cascade it away silently. usp_SetActiveCompanyProfile
        -- validates on the way IN instead, which is where an operator can still be told why.
        ProfileKey nvarchar(128)     NOT NULL,
        UpdatedAt  datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ActiveCompanyProfile_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy  nvarchar(128)     NULL
    );
END
GO

-- Per-column guards, same reason as every other table in this script: the CREATE TABLE above
-- does nothing on a database that already has an earlier revision.
IF COL_LENGTH(N'monitoring.ActiveCompanyProfile', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.ActiveCompanyProfile ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_ActiveCompanyProfile_ProfileKey DEFAULT N'';
GO
IF COL_LENGTH(N'monitoring.ActiveCompanyProfile', N'UpdatedAt') IS NULL
    ALTER TABLE monitoring.ActiveCompanyProfile ADD UpdatedAt datetimeoffset(7) NOT NULL
        CONSTRAINT DF_monitoring_ActiveCompanyProfile_UpdatedAt DEFAULT SYSDATETIMEOFFSET();
GO
IF COL_LENGTH(N'monitoring.ActiveCompanyProfile', N'UpdatedBy') IS NULL
    ALTER TABLE monitoring.ActiveCompanyProfile ADD UpdatedBy nvarchar(128) NULL;
GO

-- No Enabled join: this returns the pointer as stored, so a profile disabled AFTER it was
-- activated still comes back and CompanyAdapterProvider throws by name. Quietly falling through
-- to the configured default would monitor a DIFFERENT customer than the operator selected, which
-- is worse than a loud stop - the same call the rest of this file makes.
CREATE OR ALTER PROCEDURE monitoring.usp_GetActiveCompanyProfile
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProfileKey, UpdatedAt, UpdatedBy
    FROM monitoring.ActiveCompanyProfile
    WHERE LockId = 1;
END
GO

-- Rejects a key that is not a registered, ENABLED profile. Activating a company that does not
-- exist would leave every scout cycle failing with "no enabled company profile is registered
-- under X" and nothing to connect that to the switch that caused it; refusing here is the only
-- point where the operator who typed it is still around to be told.
--
-- MERGE WITH (HOLDLOCK) rather than IF EXISTS + UPDATE, for the same race usp_UpsertCompanyProfile
-- avoids: two operators switching at once would otherwise both see "no row" and both INSERT.
CREATE OR ALTER PROCEDURE monitoring.usp_SetActiveCompanyProfile
    @ProfileKey nvarchar(128), @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM monitoring.CompanyProfile WHERE ProfileKey = @ProfileKey AND Enabled = 1)
    BEGIN
        DECLARE @Message nvarchar(400) =
            N'Cannot activate company profile ''' + ISNULL(@ProfileKey, N'(null)') +
            N''': no row with that ProfileKey exists in monitoring.CompanyProfile, or it is disabled.';
        THROW 50000, @Message, 1;
    END

    MERGE monitoring.ActiveCompanyProfile WITH (HOLDLOCK) AS target
    USING (SELECT 1 AS LockId) AS source
        ON target.LockId = source.LockId
    WHEN MATCHED THEN
        UPDATE SET ProfileKey = @ProfileKey, UpdatedAt = SYSDATETIMEOFFSET(), UpdatedBy = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (LockId, ProfileKey, UpdatedAt, UpdatedBy)
        VALUES (1, @ProfileKey, SYSDATETIMEOFFSET(), @ChangedBy);

    SELECT ProfileKey, UpdatedAt, UpdatedBy
    FROM monitoring.ActiveCompanyProfile
    WHERE LockId = 1;
END
GO
