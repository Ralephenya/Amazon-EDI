# Testing status and how to verify this

## Nothing here has been compiled or run

The code was written in an environment with no .NET SDK and no way to install one. **39 test cases
exist as source; zero have ever executed.** Treat the whole solution as unverified until step 1
passes on a real machine. This is not a hedge - expect compile errors on the first build.

## Step 1 - build and go green

```bash
dotnet build
dotnet test
```

Most likely to break first:

- `TreatWarningsAsErrors` is on in `Directory.Build.props`, so any nullability warning fails the build.
- Package versions were written from memory and have never been restored.
- `Jumbo.AmazonEdi.sln` was hand-written with generated GUIDs and has never been opened by MSBuild.
- `InternalsVisibleTo` on the Omni project is what lets `OmniInvoiceMapperTests` see the row types.

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
| Repository incl. duplicate rejection, on SQLite (`EfAmazonInvoiceRepositoryTests`) | The startup migration path (`DatabaseMigrator`) |
| Our models vs Amazon's published schema (`InvoicePayloadContractTests`) | Jumbo Hub wiring - not written yet |

The right-hand column is deliberate. Those are integration concerns needing a real database, real
credentials, or code that does not exist yet; they are covered by the manual steps below rather than
by more unit tests.

Two notes on choices that look odd but are not:

- **Repository tests run on SQLite in-memory, not the EF InMemory provider.** InMemory does not
  enforce unique indexes, so it would let the duplicate-submission test pass while the real guard was
  broken.
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
