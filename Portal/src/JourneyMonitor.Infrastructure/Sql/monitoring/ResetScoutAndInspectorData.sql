-- Manual, destructive reset of Scout (latency + exception boards) and Inspector generated data.
-- NOT part of automatic provisioning - init-sql.sh does not reference this file. Run by hand,
-- once, against one database at a time, e.g.:
--   docker exec -i jm-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -I -b -d platformDB -i /scripts/monitoring/ResetScoutAndInspectorData.sql
--
-- In scope: everything Scout/Inspector generate at runtime - board cycles, handoff packages, the
-- shared work queue, forensic cases, their agent transcripts, and their notification history, plus
-- the "last run" timestamps so the Simian Army cards read as never-run.
--
-- Out of scope on purpose: monitoring.RcaReport and any ForensicCase it references (kept whole,
-- including that case's AgentTurn/AgentToolCall transcript, AgentWorkQueue entry and originating
-- InvestigationPackage); every *Setting/*Budget/*Pause configuration table; Finding/DoctorReport
-- (the older latency-spike-monkey/doctor-monkey agents, out of this request's scope); and the
-- knowledge base.
SET NOCOUNT ON;

BEGIN TRANSACTION;

-- Forensic cases that already have a published RCA - these, and everything that feeds them, survive.
DECLARE @CasesToKeep TABLE (Id uniqueidentifier PRIMARY KEY);
INSERT INTO @CasesToKeep (Id)
SELECT DISTINCT ForensicCaseId FROM monitoring.RcaReport;

DECLARE @PackagesToKeep TABLE (Id uniqueidentifier PRIMARY KEY);
INSERT INTO @PackagesToKeep (Id)
SELECT DISTINCT fc.InvestigationPackageId
FROM monitoring.ForensicCase fc
WHERE fc.Id IN (SELECT Id FROM @CasesToKeep);

-- Inspector transcript, deepest first.
DELETE atc
FROM monitoring.AgentToolCall atc
INNER JOIN monitoring.AgentTurn atu ON atu.Id = atc.AgentTurnId
WHERE atu.ForensicCaseId NOT IN (SELECT Id FROM @CasesToKeep);

DELETE FROM monitoring.AgentTurn
WHERE ForensicCaseId NOT IN (SELECT Id FROM @CasesToKeep);

-- Inspector's own escalation/notification history for the cases being removed.
DELETE FROM monitoring.NotificationLog
WHERE SourceType = N'forensic-case'
  AND SourceId NOT IN (SELECT Id FROM @CasesToKeep);

-- The cases themselves.
DELETE FROM monitoring.ForensicCase
WHERE Id NOT IN (SELECT Id FROM @CasesToKeep);

-- The generic monkey-to-monkey queue - scout packages handed off to the Inspector.
DELETE FROM monitoring.AgentWorkQueue
WHERE PayloadType = N'investigation-package'
  AND TRY_CAST(PayloadId AS uniqueidentifier) NOT IN (SELECT Id FROM @PackagesToKeep);

-- The Scout's own LLM call log, keyed 1:1 by package.
DELETE FROM monitoring.ScoutLlmCallLog
WHERE InvestigationPackageId NOT IN (SELECT Id FROM @PackagesToKeep);

-- The handoff packages for both boards.
DELETE FROM monitoring.InvestigationPackage
WHERE Board IN (N'latency', N'exception')
  AND Id NOT IN (SELECT Id FROM @PackagesToKeep);

-- Cycle history for both boards - not tied to any individual case, always cleared.
DELETE FROM monitoring.ScoutRun
WHERE Board IN (N'latency', N'exception');

-- "Last run" timestamps so the Simian Army cards read as never-run, like a fresh environment.
DELETE FROM monitoring.MonkeyRun
WHERE MonkeyKey IN (N'latency-scout-monkey', N'exception-scout-monkey', N'inspector-monkey');

COMMIT TRANSACTION;

SELECT 'ForensicCase' AS TableName, COUNT(*) AS RemainingRows FROM monitoring.ForensicCase
UNION ALL SELECT 'InvestigationPackage', COUNT(*) FROM monitoring.InvestigationPackage
UNION ALL SELECT 'ScoutRun', COUNT(*) FROM monitoring.ScoutRun
UNION ALL SELECT 'AgentTurn', COUNT(*) FROM monitoring.AgentTurn
UNION ALL SELECT 'AgentToolCall', COUNT(*) FROM monitoring.AgentToolCall
UNION ALL SELECT 'AgentWorkQueue (investigation-package)', COUNT(*) FROM monitoring.AgentWorkQueue WHERE PayloadType = N'investigation-package'
UNION ALL SELECT 'ScoutLlmCallLog', COUNT(*) FROM monitoring.ScoutLlmCallLog
UNION ALL SELECT 'RcaReport (untouched)', COUNT(*) FROM monitoring.RcaReport
UNION ALL SELECT 'MonkeyRun (scout/inspector keys)', COUNT(*) FROM monitoring.MonkeyRun
    WHERE MonkeyKey IN (N'latency-scout-monkey', N'exception-scout-monkey', N'inspector-monkey');
