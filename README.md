# Jumbo Brands - Amazon EDI (Vendor Central 1P)

Automates the invoice submission Erica does by hand in Vendor Central today: read invoices from
Omni, build the Amazon document, and submit it.

Despite the name "Amazon EDI", this uses Amazon's **Selling Partner API** (Vendor Retail Procurement
Invoices), not classic X12/EDIFACT over AS2 or SFTP. No VAN, no per-document fees, no 810 mapper -
one REST call: `POST /vendor/payments/v1/invoices`.

**Phase 1 is invoice submission only.** POs are still captured into Omni by hand. PO pull,
acknowledgements and ASNs are Phase 2+.

## Layout

| Project | What it does |
|---|---|
| `src/Jumbo.AmazonEdi.Core` | Amazon wire models, `InvoiceBuilder`, `InvoiceValidator`, options. No I/O. |
| `src/Jumbo.AmazonEdi.SpApi` | LWA token client, `VendorInvoicesClient`. Nothing Jumbo-specific. |
| `src/Jumbo.AmazonEdi.Omni` | Reads invoices out of Omni: Dapper over one stored procedure. Read-only. |
| `src/Jumbo.AmazonEdi.Persistence` | EF Core state store (code-first migrations) and the on-disk payload archive. |
| `src/Jumbo.AmazonEdi.Jobs` | `AmazonInvoiceSubmissionJob` - the pipeline. |
| `tools/Jumbo.AmazonEdi.Runner` | Console host for development and dry runs. |
| `tests/Jumbo.AmazonEdi.Tests` | Unit tests, including a contract check against Amazon's published model. |
| `docs/omni-stored-procedure.md` | The contract for the Omni stored procedure. |
| `docs/testing.md` | What is tested, what is not, and how to verify the whole thing. |

## Hosting in Jumbo Hub

The job is host-agnostic on purpose. In Jumbo Hub:

```csharp
services.AddAmazonEdi(configuration);

// After the host is built, before scheduling: applies pending EF Core migrations,
// or throws if AutoMigrate is off and the schema is behind.
await app.Services.EnsureAmazonEdiDatabaseAsync();

RecurringJob.AddOrUpdate<AmazonInvoiceSubmissionJob>(
    "amazon-invoices",
    job => job.RunAsync(CancellationToken.None),
    "*/15 * * * *");
```

Hangfire supplies retry, history and the dashboard, so none of that is rebuilt here.

If Jumbo Hub is ever scaled beyond one instance, set `AmazonEdi:Persistence:AutoMigrate` to false and
run migrations as a deploy step - two instances migrating the same database concurrently is a real
hazard. With it off, startup fails fast when migrations are pending rather than running against a
stale schema.

## Database

The schema is EF Core code-first. There is no hand-maintained SQL file - the model is the source of
truth, and pending migrations are applied at startup.

The first migration has not been generated yet (the code was written in an environment with no .NET
SDK). Create it once, then commit it:

```bash
dotnet ef migrations add InitialAmazonEdiSchema \
  --project src/Jumbo.AmazonEdi.Persistence \
  --startup-project tools/Jumbo.AmazonEdi.Runner
```

After a model change, add another migration the same way. To apply them by hand rather than at
startup: `dotnet ef database update` with the same two project arguments.

## Running locally

```bash
dotnet build
dotnet test
dotnet run --project tools/Jumbo.AmazonEdi.Runner
```

Credentials never go in `appsettings.json`. Use user-secrets or environment variables:

```bash
dotnet user-secrets --project tools/Jumbo.AmazonEdi.Runner set "AmazonEdi:SpApi:Lwa:ClientId"     "amzn1.application-oa2-client..."
dotnet user-secrets --project tools/Jumbo.AmazonEdi.Runner set "AmazonEdi:SpApi:Lwa:ClientSecret" "..."
dotnet user-secrets --project tools/Jumbo.AmazonEdi.Runner set "AmazonEdi:SpApi:Lwa:RefreshToken" "Atzr|..."
```

## Testing status

**Nothing in this repository has been compiled or run yet** - see
[`docs/testing.md`](docs/testing.md) for what that means, what the tests cover, and the verification
sequence.

## Before the first live invoice

1. Fill in the discovery items in [`docs/discovery.md`](docs/discovery.md). Nothing works without them.
2. Write the Omni stored procedure to the contract in
   [`docs/omni-stored-procedure.md`](docs/omni-stored-procedure.md), and generate the first EF
   migration (see **Database** above).
3. Keep `AmazonEdi:SpApi:UseSandbox` true and confirm the sandbox round-trip.
4. Dry run against real Omni data with the sandbox still on, and compare every generated payload
   against what was actually keyed into Vendor Central for the same invoices.
5. Parallel test with Amazon - invoice both ways until at least three files validate.
6. Only then set `Mode` to `Live` and `UseSandbox` to false.

## Design notes

- **Duplicate submission is impossible by construction.** The unique constraint on
  `OmniInvoiceNumber` is the idempotency key, and the row is written before Amazon is called.
- **The Omni invoice is the source of truth, never the PO.** Lines get short-shipped or declined, so
  PO quantities are not authoritative - which is why invoice-only automation works without also
  automating POs.
- **Per-invoice isolation.** One bad invoice never stops the batch.
- **Everything is archived verbatim**, request and response, to the database and to disk.
- **"Nothing to send" and "the call failed" are logged differently.** Conflating those two is what
  made the old Checkers integration so hard to diagnose.
- **A human approves before anything is sent** while `RequireApproval` is on. That gate is a feature.
