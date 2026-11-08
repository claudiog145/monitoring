-- Staging hotel hosts. Run only against the QA database (platformDBQA locally).
-- CreateBrandTables.sql always writes the production URLs; this puts the UAT hosts back
-- on the brands that have a host in the staging hotel App Insights app.
-- T-Mobile UAT is tmobile.saveonuat.com, not stays.tmobiletravel.saveonuat.com.
-- Synchrony Travel shares travel.mysynchronytravel.com in production. In UAT, club 15491
-- is travel.synchronytravel.saveonuat.com (search, rooms, and purchase). The other
-- Synchrony clubs stay on travel.mysynchronytravel.saveonuat.com.
-- Smarter Getaways club 15668 is the catalog id on vacationowners1.saveonuat.com.
-- The other Smarter Getaways clubs stay on members.smartergetaways.com.
UPDATE brand.Information
SET Url = N'tmobile.saveonuat.com'
WHERE ClubId = 15639;
GO

UPDATE brand.Information
SET Url = N'travel.mysynchronytravel.saveonuat.com'
WHERE Url = N'travel.mysynchronytravel.com';
GO

UPDATE brand.Information
SET Url = N'travel.synchronytravel.saveonuat.com'
WHERE ClubId = 15491;
GO

UPDATE brand.Information
SET Url = N'vacationowners1.saveonuat.com'
WHERE ClubId = 15668;
GO
