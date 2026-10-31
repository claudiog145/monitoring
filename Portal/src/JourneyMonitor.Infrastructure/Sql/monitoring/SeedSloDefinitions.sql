-- Seeds the 28 recommended SLO definitions (research in
-- docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md /
-- docs/sessions/assets/slo-seed-recommended.json) so a fresh database already has them instead of
-- requiring someone to run docs/sessions/assets/seed-slo-definitions.ps1 by hand against a live
-- API. Idempotent and non-destructive: only fills rows that don't exist yet for
-- (Domain, StageKey, SignalType) - never overwrites a definition someone already tuned from the
-- Settings tab (no UPDATE branch).

MERGE monitoring.SloDefinition WITH (HOLDLOCK) AS target
USING (VALUES
    (N'hotels', N'01 Login',             N'latency', 900,   99.00, 28, 1),
    (N'hotels', N'02 Hotel Search',      N'latency', 5000,  99.00, 28, 1),
    (N'hotels', N'03 Hotel Results',     N'latency', 100,   99.00, 28, 1),
    (N'hotels', N'04 Hotel Details',     N'latency', 10000, 99.00, 28, 1),
    (N'hotels', N'05 PreBook',           N'latency', 2500,  99.00, 28, 1),
    (N'hotels', N'06 Checkout',          N'latency', 3500,  99.00, 28, 1),
    (N'hotels', N'07 Booking',           N'latency', 24000, 99.00, 28, 1),
    (N'hotels', N'08 Confirmation',      N'latency', 3500,  99.00, 28, 1),

    (N'cars', N'01 Login',               N'latency', 1400,  99.00, 28, 1),
    (N'cars', N'02 Car Search',          N'latency', 8000,  99.00, 28, 1),
    (N'cars', N'03 Car Results',         N'latency', 2400,  99.00, 28, 1),
    (N'cars', N'05 PreBook',             N'latency', 1400,  99.00, 28, 1),
    (N'cars', N'06 Checkout',            N'latency', 4600,  99.00, 28, 1),
    (N'cars', N'07 Booking',             N'latency', 24000, 99.00, 28, 1),
    (N'cars', N'08 Confirmation',        N'latency', 3800,  99.00, 28, 1),

    (N'cruises', N'01 T-Mobile SSO',     N'latency', 3500,  99.00, 28, 1),
    (N'cruises', N'02 Cruise Listing',   N'latency', 6000,  99.00, 28, 1),
    (N'cruises', N'03 Cruise Detail',    N'latency', 1800,  99.00, 28, 1),
    (N'cruises', N'04 Rooms & Rates',    N'latency', 2000,  99.00, 28, 1),
    (N'cruises', N'05 Total Price',      N'latency', 4000,  99.00, 28, 1),
    (N'cruises', N'06 Cabin Selection',  N'latency', 50,    99.00, 28, 1),
    (N'cruises', N'09 Payment',          N'latency', 3500,  99.00, 28, 1),

    (N'air', N'01 Flight Search',        N'latency', 25000, 99.00, 28, 1),
    (N'air', N'02 Review Your Trip',     N'latency', 9000,  99.00, 28, 1),
    (N'air', N'03 Add Traveler Info',    N'latency', 1200,  99.00, 28, 1),
    (N'air', N'04 Choose Your Seat',     N'latency', 2400,  99.00, 28, 1),
    (N'air', N'05 Review & Pay',         N'latency', 1100,  99.00, 28, 1),
    (N'air', N'06 Confirmation',         N'latency', 2500,  99.00, 28, 1)
) AS source (Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled)
ON target.Domain = source.Domain
   AND target.StageKey = source.Stage
   AND target.SignalType = source.SignalType
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled, UpdatedBy)
    VALUES (source.Domain, source.Stage, source.SignalType, source.TargetThresholdMs, source.TargetPercentage,
            source.WindowDays, source.Enabled, N'system:seed-recommended-slos');
GO
