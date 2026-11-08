# Exceptions

The Exceptions tab lists errors customers hit while booking hotels, cruises, and cars. Each row is one repeating problem, not one individual error.

## What we capture

We keep an error when we can tell which brand it belongs to.

- **Hotels and cars:** a known club, or a car page on a brand website we already monitor.
- **Cruises:** a known partner.

Each row records:

- Where it happened: hotels, cruises, or cars, and which club or partner.
- What failed, and whether it came from the **server** or the customer's **browser**.
- How often it has happened, how many people it affected, and when it was last seen.
- Which booking step it belongs to, such as cruise listing or car search.

The same error, for the same brand and the same step, stays as one row. A new time it happens is added to that row.

Severity follows how often it has happened:

| Times seen | Severity |
| --- | --- |
| 100 or more | Critical |
| 25 or more | High |
| 5 or more | Medium |
| Fewer than 5 | Low |

We do not keep errors we cannot tie to a brand. We also skip noisy browser events that are not a real booking failure.

## How we capture it

Every few minutes the portal reads the latest errors from application monitoring.

A browser error is labeled **Browser**. Everything else is labeled **Server**.

If that error is already on the list, we add the new time it happened and refresh the count, the last-seen time, and the source. The source shown on the row is the source of the most recent time it happened. Older times keep the source they had when they were stored.

If it is new, we add a row.

## How the portal shows it

Open the **Exceptions** tab. The table shows:

Exception ID, when it last happened, severity, domain, club, booking stage, error type, source (Server or Browser), how many times it happened, how many people were affected, and status.

Select a row to see the message, the recent times it happened, and the related customer journeys.
