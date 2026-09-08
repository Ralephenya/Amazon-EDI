# Jumbo Brands - Amazon EDI

Automates the invoice submission that is done by hand in Amazon Vendor Central today: read invoices
from Omni, build the Amazon document, submit it.

## What this is, in one paragraph

Jumbo Brands is a **1P vendor** to Amazon (Vendor Central: Amazon raises POs, we invoice them).
Despite the name "Amazon EDI", this integration uses the **Selling Partner API** - specifically
`POST /vendor/payments/v1/invoices` - not X12/EDIFACT over AS2 or SFTP. Phase 1 is **invoice
submission only**; POs are still captured into Omni by hand. This code is destined to be hosted
inside **Jumbo Hub** as a Hangfire recurring job (see `docs/porting-to-jumbo-hub.md`).

## Rules that must not be broken

These are not style preferences. Each one exists because breaking it costs money or credibility with
Amazon.

1. **Never let an invoice be submitted twice.** The unique index on `InvoiceRecord.OmniInvoiceNumber`
   is the idempotency key, and the row is written *before* Amazon is called. Do not replace it with
   an application-side "check then insert" - the system this replaces had exactly that bug.
2. **The Omni invoice is the source of truth, never the Amazon PO.** Lines get short-shipped or
   declined, so PO quantities are not authoritative.
3. **Never recompute Omni's money.** Copy the figures across. `InvoiceValidator` reconciles lines
   against the header and holds anything off by more than a cent; that check is worthless if we
   invented one side of it. Money columns are `decimal(19,4)` explicitly for the same reason.
4. **A failure must say something a human can act on.** No generic "validation failed". The messages
   in `InvoiceValidator` are written to be read by Erica, not by a developer with a debugger.
5. **"Nothing to send" and "the call failed" must never look the same in the log.** Conflating those
   two is what made the previous Checkers integration so hard to diagnose.
6. **Archive every request and response verbatim**, to the database and to disk.
7. **Nothing in the UI may resend an invoice Amazon already has.** Enforced in
   `EfInvoiceReviewService`, tested, and not to be relaxed.

## Layout

| Project | Role |
|---|---|
| `Jumbo.AmazonEdi.Core` | Amazon wire models, `InvoiceBuilder`, `InvoiceValidator`, options, interfaces. **No I/O.** |
| `Jumbo.AmazonEdi.SpApi` | LWA token client, `VendorInvoicesClient`. Nothing Jumbo-specific. |
| `Jumbo.AmazonEdi.Omni` | Reads Omni: Dapper over one stored procedure. Read-only. |
| `Jumbo.AmazonEdi.Persistence` | EF Core state store, review service, payload archive. |
| `Jumbo.AmazonEdi.Jobs` | `AmazonInvoiceSubmissionJob` - the pipeline. |
| `Jumbo.AmazonEdi.Runner` | Console host for development and dry runs. |

Dependencies flow one way: `Core` depends on nothing; everything depends on `Core`; `Jobs` composes.

## Conventions

- .NET 8, nullable enabled, `TreatWarningsAsErrors`.
- **Omni access is Dapper + stored procedure. Our own tables are EF Core code-first.** Do not mix
  these up: EF models what we own, Dapper reads what we do not.
- Comments explain *why*, especially where the code looks odd on purpose. Do not add comments that
  restate the code.
- Tests use xunit. Persistence tests run against **real SQL Server** (`AMAZONEDI_TEST_SQL`), and
  skip when none is configured. Not the EF InMemory provider - it does not enforce unique indexes and
  would hide a broken idempotency guard - and not SQLite, which cannot order a `DateTimeOffset`.
- Secrets (`AmazonEdi:SpApi:Lwa:*`) come from user-secrets or environment variables. Never
  `appsettings.json`.

## State of play

CI builds and runs the full suite on every push against a SQL Server service container: currently
**clean build, 78 tests passing**. That is the limit of what is verified - nothing has yet talked to
Amazon, to Omni, or to a migrated database. Read `docs/testing.md` before trusting anything further.

Still outstanding: the Omni stored procedure (`docs/omni-stored-procedure.md`), the discovery items
(`docs/discovery.md`, especially which Omni field holds the Amazon PO number), the first EF
migration, and the Jumbo Hub pages (`docs/porting-to-jumbo-hub.md`).

Check CI before claiming anything compiles or passes.
