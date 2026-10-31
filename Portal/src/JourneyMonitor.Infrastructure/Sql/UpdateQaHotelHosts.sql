-- Staging hotel hosts. Run only against the QA database (platformDBQA locally).
-- CreateBrandTables.sql always writes the production URLs; this puts the UAT hosts back
-- on the two brands that have a host in the staging hotel App Insights app.
-- T-Mobile UAT is tmobile.saveonuat.com, not stays.tmobiletravel.saveonuat.com.
-- Both Synchrony Travel rows share travel.mysynchronytravel.com in production and
-- travel.mysynchronytravel.saveonuat.com in UAT.
UPDATE brand.Information
SET Url = N'tmobile.saveonuat.com'
WHERE ClubId = 15639;
GO

UPDATE brand.Information
SET Url = N'travel.mysynchronytravel.saveonuat.com'
WHERE Url = N'travel.mysynchronytravel.com';
GO
