-- Normalized Product catalog + Maturity Status for the Club Map (US 239295 / bridge to 235561).
-- brand.ClubInventory stays one row per ClubId (identity).
-- brand.ClubProductMaturityStatus holds Product links and Status 0-3 per club x product.
-- Idempotent for docker init / re-apply.

IF SCHEMA_ID(N'brand') IS NULL
    EXEC(N'CREATE SCHEMA brand');
GO

IF OBJECT_ID(N'brand.Product', N'U') IS NULL
BEGIN
    CREATE TABLE brand.Product
    (
        ProductId   INT            NOT NULL
            CONSTRAINT PK_brand_Product PRIMARY KEY,
        Name        NVARCHAR(50)   NOT NULL
            CONSTRAINT UQ_brand_Product_Name UNIQUE,
        SortOrder   INT            NOT NULL
            CONSTRAINT DF_brand_Product_SortOrder DEFAULT (0)
    );
END
GO

IF OBJECT_ID(N'brand.MaturityLevel', N'U') IS NULL
BEGIN
    CREATE TABLE brand.MaturityLevel
    (
        MaturityLevelId INT            NOT NULL
            CONSTRAINT PK_brand_MaturityLevel PRIMARY KEY,
        Code            NVARCHAR(50)   NOT NULL
            CONSTRAINT UQ_brand_MaturityLevel_Code UNIQUE,
        DisplayName     NVARCHAR(100)  NOT NULL,
        Description     NVARCHAR(400)  NULL
    );
END
GO

IF OBJECT_ID(N'brand.ClubProductMaturityStatus', N'U') IS NULL
BEGIN
    CREATE TABLE brand.ClubProductMaturityStatus
    (
        ClubId          INT NOT NULL,
        ProductId       INT NOT NULL,
        MaturityLevelId INT NOT NULL
            CONSTRAINT DF_brand_ClubProductMaturityStatus_Level DEFAULT (0),
        CONSTRAINT PK_brand_ClubProductMaturityStatus PRIMARY KEY (ClubId, ProductId),
        CONSTRAINT FK_ClubProductMaturityStatus_Club
            FOREIGN KEY (ClubId) REFERENCES brand.ClubInventory (ClubId),
        CONSTRAINT FK_ClubProductMaturityStatus_Product
            FOREIGN KEY (ProductId) REFERENCES brand.Product (ProductId),
        CONSTRAINT FK_ClubProductMaturityStatus_Level
            FOREIGN KEY (MaturityLevelId) REFERENCES brand.MaturityLevel (MaturityLevelId)
    );
END
GO

-- Catalog rows (safe re-run).
MERGE brand.Product AS t
USING (VALUES
    (1, N'Cars', 1),
    (2, N'Cruises', 2),
    (3, N'Hotels', 3),
    (4, N'Flights', 4)
) AS s (ProductId, Name, SortOrder)
ON t.ProductId = s.ProductId
WHEN MATCHED THEN UPDATE SET Name = s.Name, SortOrder = s.SortOrder
WHEN NOT MATCHED THEN INSERT (ProductId, Name, SortOrder) VALUES (s.ProductId, s.Name, s.SortOrder);
GO

MERGE brand.MaturityLevel AS t
USING (VALUES
    (0, N'NotObserved', N'Not Observed', N'No telemetry exists or not centralized.'),
    (1, N'Observed', N'Observed', N'Telemetry available in Eventhouse. Basic dashboards and metrics.'),
    (2, N'AgenticallyObserved', N'Agentically Observed', N'Correlated analysis and recommendations with agentic insights (RCA, patterns, anomalies).'),
    (3, N'AlertingSelfHealing', N'Alerting & Self-Healing', N'Proactive alerts with automated remediation or self-healing actions.')
) AS s (MaturityLevelId, Code, DisplayName, Description)
ON t.MaturityLevelId = s.MaturityLevelId
WHEN MATCHED THEN UPDATE SET Code = s.Code, DisplayName = s.DisplayName, Description = s.Description
WHEN NOT MATCHED THEN INSERT (MaturityLevelId, Code, DisplayName, Description)
    VALUES (s.MaturityLevelId, s.Code, s.DisplayName, s.Description);
GO
