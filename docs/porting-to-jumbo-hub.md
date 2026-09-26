# Moving this into Jumbo Hub

This repository is deliberately host-agnostic. Nothing in it knows about Hangfire, Blazor, MVC or
Jumbo Hub. Porting it is therefore mostly *referencing* rather than *rewriting*.

Read this alongside `CLAUDE.md` (what the project is and the rules that must not be broken) and
`docs/testing.md` (what has actually been verified).

## What moves, and what does not

**Moves as-is** - copy the five `src/` projects into Jumbo Hub's solution, or reference them as a
submodule/NuGet package. No code changes:

- `Jumbo.AmazonEdi.Core`
- `Jumbo.AmazonEdi.SpApi`
- `Jumbo.AmazonEdi.Omni`
- `Jumbo.AmazonEdi.Persistence`
- `Jumbo.AmazonEdi.Jobs`

**Does not move**: `tools/Jumbo.AmazonEdi.Runner`. It is a development host - keep it in this repo
for dry runs against the sandbox. It is useful precisely because it exercises the same job outside
the Hub.

**Has to be written in Jumbo Hub** (the only genuinely new work):

1. The Hangfire recurring-job registration - about five lines.
2. Two pages: an approval queue and an invoice history/detail view.
3. Alerting, if Hub has a house mechanism for it.

## Step 1 - registration

```csharp
// Program.cs / Startup
services.AddAmazonEdi(builder.Configuration);
```

That single call wires everything: options, the SP-API HTTP clients, the Omni source, the EF context
factory, the repository, the review service, the payload archive, the builder, the validator, and the
job itself. It is in `Jumbo.AmazonEdi.Jobs/ServiceCollectionExtensions.cs`.

Then, after the host is built and **before** scheduling:

```csharp
await app.Services.EnsureAmazonEdiDatabaseAsync();

RecurringJob.AddOrUpdate<AmazonInvoiceSubmissionJob>(
    "amazon-invoices",
    job => job.RunAsync(CancellationToken.None),
    "*/15 * * * *");
```

`EnsureAmazonEdiDatabaseAsync` applies pending EF migrations, or throws naming them if
`AmazonEdi:Persistence:AutoMigrate` is off. **If Jumbo Hub runs more than one instance, turn
`AutoMigrate` off** and migrate as a deploy step - concurrent migrations are a real hazard.

`RunAsync` returns a `JobRunSummary` (ingested / validated / held / submitted / failed). Hangfire
ignores the return value; use it if you want a job-level alert.

## Step 2 - configuration

Copy the `AmazonEdi` section from `tools/Jumbo.AmazonEdi.Runner/appsettings.json` into Hub's config
and fill it in. Two connection strings, and they are **not** the same database:

- `AmazonEdi:Omni:ConnectionString` - the SQL Server hosting the `JOLLYJUMBO` linked server.
- `AmazonEdi:Persistence:ConnectionString` - where the `AmazonEdi` schema lives. The Hub database is
  a fine home for it; the schema keeps the tables clearly ours.

The three LWA secrets go in user-secrets, environment variables, or whatever Hub already uses for
secrets - **never in `appsettings.json`**.

## Step 3 - the two pages

Both are thin. All the logic is behind `IInvoiceReviewService`
(`Core/Abstractions/Review.cs`, implemented in `Persistence/EfInvoiceReviewService.cs`), which is
already tested - the pages should not re-implement any of it.

**Approval queue** - the screen that replaces Erica's manual check:

```csharp
var queue = await review.GetApprovalQueueAsync(100, ct);   // InvoiceSummary
await review.ApproveAsync(id, User.Identity!.Name!, ct);   // -> Validated, sent next run
await review.SkipAsync(id, user, reason, ct);              // reason is required
await review.RetryAsync(id, user, ct);                     // -> Pending, re-validated
```

Show, per invoice: invoice number, Amazon PO, date, line count, total including tax, and `LastError`
when present. On the detail view show the lines beside the PO, so approving is a real check rather
than a rubber stamp - that comparison is the entire point of the gate.

Every action returns `ReviewActionResult`. A refusal is normal (two people working the same queue),
so render `Reason` to the user rather than treating it as an error. **Do not add a UI path that can
resend a `Submitted` or `Accepted` invoice** - the service refuses it, and it must stay refused.

**History / diagnostics**: `GetRecentAsync`, `GetFailedAsync`, and `GetDetailAsync` for one invoice
with its lines and every attempt, including the raw request and response JSON. Hangfire's own
dashboard covers job-level runs, so these pages only need the invoice-level view it cannot give.

## Step 4 - what to verify after the move

1. Hub starts, and the log shows either "schema is up to date" or the migrations it applied.
2. The Hangfire dashboard shows `amazon-invoices` scheduled, and a manual trigger completes.
3. With `RequireApproval: true` and `UseSandbox: true`, invoices appear in the approval queue and
   **nothing is sent**.
4. Approving one moves it to `Validated`, and the next run submits it to the sandbox.
5. `docs/testing.md` step 3 onwards - the sandbox round-trip, the dry run, and the payload diff
   against what was really keyed into Vendor Central.

## A note for whoever picks this up

Do not "simplify" the human approval gate away, and do not move the review rules into a page. The
gate is a deliberate design decision carried over from the ShopRite/Pick n Pay integrations, and the
rules are in a service precisely so a button cannot bypass them.
