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
| `src/Jumbo.AmazonEdi.Omni` | Reads invoices out of Omni. Read-only. |
| `src/Jumbo.AmazonEdi.Persistence` | State store and the on-disk payload archive. |
| `src/Jumbo.AmazonEdi.Jobs` | `AmazonInvoiceSubmissionJob` - the pipeline. |
| `tools/Jumbo.AmazonEdi.Runner` | Console host for development and dry runs. |
| `tests/Jumbo.AmazonEdi.Tests` | Unit tests, including a contract check against Amazon's published model. |
| `db/001_AmazonEdi_Schema.sql` | Tables. Run this before the first run. |

## Hosting in Jumbo Hub

The job is host-agnostic on purpose. In Jumbo Hub:

```csharp
services.AddAmazonEdi(configuration);

RecurringJob.AddOrUpdate<AmazonInvoiceSubmissionJob>(
    "amazon-invoices",
    job => job.RunAsync(CancellationToken.None),
    "*/15 * * * *");
```

Hangfire supplies retry, history and the dashboard, so none of that is rebuilt here.

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

## Before the first live invoice

1. Fill in the discovery items in [`docs/discovery.md`](docs/discovery.md). Nothing works without them.
2. Run `db/001_AmazonEdi_Schema.sql`.
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
