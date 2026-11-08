-- Historical Business Health for the Hotels card.
-- Grain: one row per (Domain, Environment, Grain, VacationClubId, WindowStart).
-- The card compares WindowStart with the same hour seven days earlier.
-- Do not store that delta; a late reload would leave it stale.
--
-- Store Hour. Sum that day's hours when a day total is needed. Day is allowed
-- only as Grain = N'Day'. Every read must filter Grain, or Hour and Day rows
-- of the same bookings are counted twice.
--
-- Conversion is (1.0 * BookingCount) / NULLIF(SearchCount, 0).
-- Cancel rate is (1.0 * CancelCount) / NULLIF(BookingCount, 0).
-- Neither rate is stored. Net bookings are BookingCount - CancelCount
-- when both counts are present.
--
-- VacationClubId 0 is the Hotels total (all clubs), which is what the card shows.
-- A real club id is that club only. Do not also store a rolled-up 0 row for a
-- window that already has club rows, or the total is counted twice.
--
-- This table is hotel only, same rule as hotel.Journey. Cruise, air, and car
-- do not belong here.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'hotel')
    EXEC(N'CREATE SCHEMA hotel');
GO

IF OBJECT_ID(N'hotel.BusinessHealth', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.BusinessHealth
    (
        Domain          nvarchar(32)      NOT NULL
            CONSTRAINT DF_hotel_BusinessHealth_Domain DEFAULT N'hotel',
        Environment     nvarchar(32)      NOT NULL,
        Grain           nvarchar(16)      NOT NULL,
        VacationClubId  bigint            NOT NULL
            CONSTRAINT DF_hotel_BusinessHealth_VacationClubId DEFAULT (0),
        WindowStart     datetimeoffset(7) NOT NULL,
        SearchCount     int               NULL,
        BookingCount    int               NULL,
        CancelCount     int               NULL,
        Revenue         decimal(18, 2)    NULL,
        CapturedAt      datetimeoffset(7) NOT NULL
            CONSTRAINT DF_hotel_BusinessHealth_CapturedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_hotel_BusinessHealth
            PRIMARY KEY (Domain, Environment, Grain, VacationClubId, WindowStart),
        CONSTRAINT CK_hotel_BusinessHealth_Domain CHECK (Domain = N'hotel'),
        CONSTRAINT CK_hotel_BusinessHealth_Grain CHECK (Grain IN (N'Hour', N'Day')),
        CONSTRAINT CK_hotel_BusinessHealth_VacationClubId CHECK (VacationClubId >= 0),
        CONSTRAINT CK_hotel_BusinessHealth_Counts CHECK (
            (SearchCount IS NULL OR SearchCount >= 0)
            AND (BookingCount IS NULL OR BookingCount >= 0)
            AND (CancelCount IS NULL OR CancelCount >= 0))
    );
END
GO
