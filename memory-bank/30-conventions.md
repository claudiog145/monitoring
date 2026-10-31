# 30 — Conventions

House style in this codebase is unusually opinionated. Matching it is not cosmetic: several of
these rules are the mechanism by which the platform stays honest.

---

## Comments explain WHY, never WHAT

A comment here records the decision and what breaks without it. It does not restate the code.

```csharp
// Base64 is the real anti-injection boundary: its alphabet cannot close a KQL string literal.
public static string ToBase64(string value) => ...
```

Not:

```csharp
// Converts the value to base64.   ← will look wrong next to its neighbours
```

If a comment would only tell the reader what the next line already says, delete it.

---

## The distinctions that are load-bearing

### `unresolved` vs `unavailable`

- **`unresolved`** — their mapping gap. The classifier ran; the profile does not describe this row.
- **`unavailable`** — our outage. The classifier itself failed.

Collapsing them hides our failures inside their data quality. `CompanyResolution` keeps them
apart and so should you.

### `truncated` vs `fieldsShortened`

- **`truncated`** — rows are missing.
- **`fieldsShortened`** — every row is present; only long text inside them was abbreviated.

These were one flag once. A model told "this is a partial list" discounts its own evidence and
reports low confidence even when holding everything — which is exactly the bug that merging them
caused.

### `fabricated` vs `unverifiable` citations

- **`fabricated`** — the model invented an identifier.
- **`unverifiable`** — we have no index to check it against.

One is a statement about the model, the other about this platform. Never summed.

---

## Failure is never silence

A search that could not run reports **failed**, never "found nothing". A sourceless specialist
says it is sourceless. A truncated list says so, in the payload the model reads — not only on the
result record, because the model reads the payload.

The failure mode this prevents: an investigation concluding "no problems found" when the truth was
"we could not look".

---

## Fail closed

Secret-gated endpoints return 401 when the secret is **missing**, not just when it is wrong. An
unconfigured secret is not "no check configured" — it is "every write is refused". See
`SettingsWriteSecretGuard`.

---

## Defaults carry their measurement

A default value's doc comment explains the number with the evidence behind it. The pattern to
copy:

```csharp
/// Derived, not chosen. Measured on a live drill-down package: 34 rows, 2373 characters per
/// serialized row, of which StackTrace alone averaged 850 ... so the measured package needs
/// 34 x 1403 = 47702, and 48000 leaves room for the envelope's own note.
```

A number without its evidence gets changed back by the next person who disagrees with it.

---

## Company-specific literals belong in the profile

This branch exists to decouple one customer's vocabulary from the engine. A club id, a vertical
name or a company name hardcoded in `Core`/`Infrastructure` fails
`CouplingInventoryTests.NoCompanySpecificLiteralOutsideTheCompanyProfile`.

The exceptions the test allows: files under `Core/Company/` whose name contains the company, and
`Infrastructure/Kql/*.txt`.

The test is currently failing on three literals — see [`50-open-issues.md`](50-open-issues.md).
The test is right and the code is wrong; do not relax it.

---

## Profile seeds are INSERT-only

`CreateMonitoringCompanyProfile.sql` is guarded by an existence check, so editing the seed does
**not** change a database that already has the row. A profile change needs an explicit migration
block after the seed that is:

- **idempotent by shape** — it stops matching once applied, so re-running is a no-op
- **scoped** — it refuses to touch a profile an operator edited in the portal (`UpdatedBy LIKE 'seed%'`)
- **audited** — writes a `CompanyProfileAudit` row, because "why did this club start resolving on
  the 3rd" must be answerable from the same table that answers it for portal edits

There is one in that file already. Copy its shape.

---

## Tests

1110 tests, sub-second. Expect **1109 passed / 1 failed** (the known coupling failure).

When behaviour changes deliberately, **rewrite the test's expectation and its doc comment to
explain the new reasoning**. Do not relax an assertion to make it pass. If a test documents a
decision you are reversing — and several here do, at length — the new comment should say why the
old objection is now satisfied.

---

## C# specifics

- .NET 10, C# 12+. File-scoped namespaces. Primary constructors where the file already uses them.
- Collection expressions (`[...]`) are used throughout.
- Comments in English, regardless of the language of the conversation that produced them.
