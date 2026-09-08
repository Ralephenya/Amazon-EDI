# The Omni stored procedure

The integration reads invoices out of Omni by executing **one stored procedure** that returns **two
result sets**. Everything Omni-specific - the `OPENQUERY(JOLLYJUMBO, …)` calls, the table and column
names, how an invoice is recognised as belonging to Amazon - lives in this procedure. The C# side
knows nothing but the contract below, so changing how Omni is queried is a procedure change, not a
redeploy.

It is called from `OmniInvoiceSource` (`src/Jumbo.AmazonEdi.Omni/OmniInvoiceSource.cs`) via Dapper's
`QueryMultiple`, on the connection in `AmazonEdi:Omni:ConnectionString` - which points at the SQL
Server hosting the `JOLLYJUMBO` linked server, **not** at Omni itself.

## Signature

```sql
CREATE OR ALTER PROCEDURE dbo.usp_AmazonEdi_GetInvoicesToSubmit
    @Since        DATETIMEOFFSET,   -- return invoices dated on or after this
    @MaxResults   INT,              -- cap on the number of INVOICES (not rows)
    @AccountCodes NVARCHAR(400)     -- comma-separated Omni debtor codes for Amazon
AS
BEGIN
    SET NOCOUNT ON;
    ...
END
```

Split `@AccountCodes` with `STRING_SPLIT(@AccountCodes, ',')`. The values come from
`AmazonEdi:AmazonCustomerAccountCodes` in configuration.

## Result set 1 - invoice headers, one row per invoice

| Column | Type | Notes |
|---|---|---|
| `InvoiceNumber` | `NVARCHAR` | Omni invoice number. Becomes the Amazon invoice id and our idempotency key. |
| `InvoiceDate` | `DATETIMEOFFSET` | |
| `PurchaseOrderNumber` | `NVARCHAR` NULL | The **Amazon PO number** captured against the order in Omni. |
| `CustomerAccountCode` | `NVARCHAR` | The Omni debtor code it matched. |
| `WarehouseCode` | `NVARCHAR` NULL | Used to pick the ship-from party. |
| `CurrencyCode` | `CHAR(3)` | `ZAR`. |
| `TotalExcludingTax` | `DECIMAL(19,4)` | |
| `TotalTax` | `DECIMAL(19,4)` | |
| `TotalIncludingTax` | `DECIMAL(19,4)` | Gross document total. |

## Result set 2 - invoice lines, one row per line

Must cover **every** invoice returned in result set 1.

| Column | Type | Notes |
|---|---|---|
| `InvoiceNumber` | `NVARCHAR` | Ties the line back to its header. |
| `LineNumber` | `INT` | |
| `StockCode` | `NVARCHAR` | Diagnostics only - not sent to Amazon, but it is what appears in error messages. |
| `Barcode` | `NVARCHAR` NULL | Sent as `vendorProductIdentifier`. |
| `Asin` | `NVARCHAR` NULL | Sent as `amazonProductIdentifier`. At least one of barcode or ASIN must be present. |
| `PurchaseOrderNumber` | `NVARCHAR` NULL | Per-line PO where Omni carries one; falls back to the header. |
| `Quantity` | `INT` | What we actually shipped. |
| `UnitPriceExcludingTax` | `DECIMAL(19,4)` | Unit price, excluding VAT. |
| `LineTotalExcludingTax` | `DECIMAL(19,4)` | As Omni computed it. |
| `LineTax` | `DECIMAL(19,4)` | |
| `TaxRate` | `DECIMAL(9,4)` | Percentage, e.g. `15`. |

## Four rules that matter more than the shape

1. **Return Omni's figures exactly as Omni holds them.** Do not recompute or re-round totals or tax
   in the procedure. `InvoiceValidator` reconciles the lines against the header and holds any invoice
   that disagrees by more than a cent - that check is only meaningful if both sides come from Omni.

2. **The procedure does not need to know what has already been sent.** Deduplication is ours: a
   watermark on the last invoice date we saw, plus a unique constraint on the invoice number.
   Re-returning an invoice we already hold is harmless and expected.

3. **Result set 2 must cover every invoice in result set 1.** An invoice whose lines are missing is
   not dropped - it comes back with no lines and is held with "Invoice has no lines" - but that is a
   symptom, not a design.

4. **`@MaxResults` caps invoices, not rows.** Apply the `TOP` to the header selection and then return
   all lines for those invoices. Capping the joined rows would truncate the last invoice's lines and
   we would invoice Amazon short.

## Sketch

```sql
CREATE OR ALTER PROCEDURE dbo.usp_AmazonEdi_GetInvoicesToSubmit
    @Since        DATETIMEOFFSET,
    @MaxResults   INT,
    @AccountCodes NVARCHAR(400)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @accounts TABLE ([Code] NVARCHAR(20) PRIMARY KEY);
    INSERT INTO @accounts ([Code])
    SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@AccountCodes, ',') WHERE [value] <> '';

    -- Pull the candidate invoices out of Omni through the linked server, then narrow.
    -- (OPENQUERY takes a literal string, so build it with sp_executesql if it needs @Since inlined.)
    SELECT TOP (@MaxResults) ...
    INTO   #headers
    FROM   OPENQUERY(JOLLYJUMBO, '...') AS omni
    JOIN   @accounts AS a ON a.[Code] = omni.[...]
    WHERE  omni.[...] >= @Since
    ORDER BY omni.[...];

    SELECT [InvoiceNumber], [InvoiceDate], [PurchaseOrderNumber], [CustomerAccountCode],
           [WarehouseCode], [CurrencyCode], [TotalExcludingTax], [TotalTax], [TotalIncludingTax]
    FROM   #headers
    ORDER BY [InvoiceDate], [InvoiceNumber];

    SELECT lines.[InvoiceNumber], lines.[LineNumber], lines.[StockCode], lines.[Barcode], lines.[Asin],
           lines.[PurchaseOrderNumber], lines.[Quantity], lines.[UnitPriceExcludingTax],
           lines.[LineTotalExcludingTax], lines.[LineTax], lines.[TaxRate]
    FROM   OPENQUERY(JOLLYJUMBO, '...') AS lines
    JOIN   #headers AS h ON h.[InvoiceNumber] = lines.[InvoiceNumber]
    ORDER BY lines.[InvoiceNumber], lines.[LineNumber];
END
```

## Still to confirm

Which Omni field holds the Amazon PO number, and whether it is captured consistently. See
`docs/discovery.md` - this is the one item that blocks everything downstream.
