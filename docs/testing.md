# Testing status and how to verify this

## Current state: builds clean, 78 tests pass

CI builds the solution and runs the full suite on every push, with a SQL Server service container.
As of the latest run: **build clean, 78 passed, 0 failed**. Check the CI badge or the Actions tab
before trusting any claim about what works - including this file.

That covers what unit tests can cover. It does **not** mean the integration works: nothing has yet
talked to Amazon, to Omni, or to a database created by a migration rather than by `EnsureCreated`.
The gaps are listed below and are closed by the manual steps, not by more unit tests.

## Step 1 - run it locally

```bash
dotnet build
dotnet test
```

The persistence tests skip unless a SQL Server is configured:

```bash
export AMAZONEDI_TEST_SQL='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True'
```

Without it they report as skipped and the other tests still run.

## Step 2 - generate the first EF migration

It does not exist yet. It was deliberately not hand-written: the model snapshot has to match the
model exactly, or the next `migrations add` produces a bogus diff.

```bash
dotnet ef migrations add InitialAmazonEdiSchema \
  --project src/Jumbo.AmazonEdi.Persistence \
  --startup-project tools/Jumbo.AmazonEdi.Runner
```

Then read `dotnet ef migrations script` and confirm two things by eye:

- the **unique index on `OmniInvoiceNumber`** is present - it is what makes a double send impossible;
- every money column is **`decimal(19,4)`**, not `(18,2)`.

## What the unit tests cover, and what they do not

| Covered | Not covered by any test |
|---|---|
| Omni rows → invoice mapping, ordering, nulls (`OmniInvoiceMapperTests`) | The stored procedure - it does not exist yet |
| Building the Amazon payload (`InvoiceBuilderTests`) | Any real HTTP call to Amazon (`VendorInvoicesClient`) |
| Every validation rule incl. the one-cent reconciliation (`InvoiceValidatorTests`) | LWA token exchange, caching, refresh |
| The job pipeline against fakes (`AmazonInvoiceSubmissionJobTests`) | Real SQL Server behaviour - tests use SQLite |
| Repository incl. duplicate rejection, on SQL Server (`EfAmazonInvoiceRepositoryTests`) | The startup migration path (`DatabaseMigrator`) |
| Approve / skip / retry rules (`EfInvoiceReviewServiceTests`) | |
| Our models vs Amazon's published schema (`InvoicePayloadContractTests`) | Jumbo Hub wiring - not written yet |
| | The migration itself - tests use `EnsureCreated`, not `Migrate` |

The right-hand column is deliberate. Those are integration concerns needing a real database, real
credentials, or code that does not exist yet; they are covered by the manual steps below rather than
by more unit tests.

Two notes on choices that look odd but are not:

- **Persistence tests run against real SQL Server.** The EF InMemory provider does not enforce unique
  indexes, so it would let the duplicate-submission test pass while the guard that stops a double send
  to Amazon was broken; SQLite enforces that but cannot order or aggregate a `DateTimeOffset`, which
  the invoice date is. Only the real provider exercises what production does.
- **`InvoicePayloadContractTests` checks our models against a vendored copy of Amazon's own
  `vendorInvoices.json`.** If Amazon renames a field or drops an enum value, the build breaks instead
  of production. Refresh the vendored copy with:
  ```bash
  curl -o tests/Jumbo.AmazonEdi.Tests/Schema/vendorInvoices.json \
    https://raw.githubusercontent.com/amzn/selling-partner-api-models/main/models/vendor-invoices-api-model/vendorInvoices.json
  ```

## Step 3 - integration checks, in this order

1. **LWA token exchange.** Once the private vendor app is registered, run the Runner with the sandbox
   on. Getting an access token proves the credentials and the endpoint region at once.
2. **Sandbox submit.** One invoice to `sandbox.sellingpartnerapi-eu.amazon.com`. Assert a
   `transactionId` comes back and an attempt row is written.
3. **The stored procedure against real Omni**, run directly in SSMS first. Confirm the column names
   match `omni-stored-procedure.md` exactly and the figures match the invoice in Omni.
4. **Dry run.** Runner against real Omni with `UseSandbox: true` and `RequireApproval: true`. Nothing
   is sent; invoices land as `AwaitingApproval`.
5. **The real acceptance test.** Diff every generated payload in the archive against what was actually
   keyed into Vendor Central for the same invoices, over about a week. The unit tests prove the code
   does what was intended; only this proves what was intended is what Amazon expects.
6. **Parallel test** with Amazon until three invoice files validate, then `Mode: Live`.

Do not skip 5. It is the only step that catches a wrong assumption about the Amazon side, and a wrong
invoice that Amazon accepts is far more expensive than one it rejects.
