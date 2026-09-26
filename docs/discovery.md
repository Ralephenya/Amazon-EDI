# Discovery - what has to be confirmed before this can go live

None of this is guesswork we can code around. Each item below changes behaviour, and getting one
wrong means Amazon rejects invoices or, worse, accepts a wrong one.

## 1. The Amazon PO number in Omni  (blocks everything)

Amazon matches an invoice to a purchase order via `items[].purchaseOrderNumber`. POs are captured
into Omni by hand today, so:

- [ ] Which Omni field holds the Amazon PO number on an invoice? Header, line, or a reference field?
- [ ] Is the capture convention consistent across everyone who does it?
- [ ] Is it ever blank, truncated, or prefixed?

If it isn't captured reliably, the fix is a data-entry convention, not code. `InvoiceValidator`
already refuses to send an invoice with no PO number rather than letting Amazon reject it silently.

Once known, it becomes the `PurchaseOrderNumber` column of the Omni stored procedure - see
[`omni-stored-procedure.md`](omni-stored-procedure.md) for the full contract.

## 2. From Vendor Central

- [ ] Our vendor code(s) - the `remitToParty.partyId`.
- [ ] Our remit-to name and address **exactly as Amazon holds it**.
- [ ] Our VAT number.
- [ ] Amazon's bill-to party id, address and tax ID. These are on the **EDI Resources** page in
      Vendor Central. Amazon fails the call outright if the bill-to details are incomplete.
- [ ] Do we invoice in `Cases` or `Eaches`?
- [ ] Are ZA POs denominated in ZAR?
- [ ] Payment terms, if we want to send them.

## 3. API access

- [ ] Register a **private vendor application** in the Vendor Central Solution Provider Portal:
      developer profile (use cases + security controls), accept the agreements, register.
- [ ] Self-authorize it to get an LWA **refresh token**.
- [ ] Confirm the endpoint and marketplace id for South Africa. We default to the EU endpoint
      (`https://sellingpartnerapi-eu.amazon.com`) and marketplace `AE08WJ6YKNBMC`. **This is the
      single fact most likely to be wrong in third-party sources - verify it against our own
      account.**
- [ ] Confirm the sandbox is available for Vendor Invoices on our account.

Vendor Invoices needs no restricted (PII) role, and SP-API no longer requires AWS SigV4 signing -
the LWA bearer token is the whole auth story.

## 4. Amazon's parallel testing requirement

Amazon requires a parallel-run phase before API invoices are used for payment: every shipped order
is invoiced **both** in Vendor Central by hand **and** via the API, until at least **three invoice
files** are validated. Keep `Mode: ParallelTest` until Amazon confirms.

- [ ] Who at Amazon confirms validation, and how do we hear about it?

## 5. Operational

- [ ] Which Omni customer/debtor account codes represent Amazon
      (`AmazonEdi:AmazonCustomerAccountCodes`)?
- [ ] Where does the payload archive live, and who backs it up?
- [ ] Who receives failure alerts, and who approves invoices in Jumbo Hub?
