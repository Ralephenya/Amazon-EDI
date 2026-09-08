/*
    Amazon EDI - invoice submission state.

    Deliberately separate from the existing [EDI] database. The one hard rule here is the unique
    constraint on OmniInvoiceNumber: it is the idempotency key, and it is what makes a duplicate
    submission to Amazon impossible even if two runs overlap. A duplicate invoice id is a real
    financial problem, so it is enforced by the database and never by application-side "check then
    insert" logic.

    Run against the Jumbo Hub database (or a dedicated AmazonEdi database).
*/

IF SCHEMA_ID(N'AmazonEdi') IS NULL
    EXEC(N'CREATE SCHEMA [AmazonEdi]');
GO

IF OBJECT_ID(N'[AmazonEdi].[Invoice]', N'U') IS NULL
BEGIN
    CREATE TABLE [AmazonEdi].[Invoice]
    (
        [Id]                        BIGINT          IDENTITY(1,1) NOT NULL,
        [OmniInvoiceNumber]         NVARCHAR(50)    NOT NULL,
        [AmazonPurchaseOrderNumber] NVARCHAR(50)    NULL,
        [CustomerAccountCode]       NVARCHAR(20)    NOT NULL,
        [WarehouseCode]             NVARCHAR(20)    NULL,
        [InvoiceDate]               DATETIMEOFFSET  NOT NULL,
        [CurrencyCode]              CHAR(3)         NOT NULL,
        [TotalExcludingTax]         DECIMAL(19,4)   NOT NULL,
        [TotalTax]                  DECIMAL(19,4)   NOT NULL,
        [TotalIncludingTax]         DECIMAL(19,4)   NOT NULL,
        [Status]                    VARCHAR(20)     NOT NULL CONSTRAINT [DF_AmazonEdi_Invoice_Status] DEFAULT ('Pending'),
        [TransactionId]             NVARCHAR(100)   NULL,
        [AttemptCount]              INT             NOT NULL CONSTRAINT [DF_AmazonEdi_Invoice_AttemptCount] DEFAULT (0),
        [LastError]                 NVARCHAR(MAX)   NULL,
        [ApprovedBy]                NVARCHAR(100)   NULL,
        [ApprovedAtUtc]             DATETIME2(3)    NULL,
        [SubmittedAtUtc]            DATETIME2(3)    NULL,
        [CreatedAtUtc]              DATETIME2(3)    NOT NULL CONSTRAINT [DF_AmazonEdi_Invoice_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc]              DATETIME2(3)    NOT NULL CONSTRAINT [DF_AmazonEdi_Invoice_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_AmazonEdi_Invoice] PRIMARY KEY CLUSTERED ([Id]),
        -- The idempotency key. Do not drop this.
        CONSTRAINT [UQ_AmazonEdi_Invoice_OmniInvoiceNumber] UNIQUE ([OmniInvoiceNumber]),
        CONSTRAINT [CK_AmazonEdi_Invoice_Status] CHECK ([Status] IN
            ('Pending','Validated','AwaitingApproval','Submitted','Accepted','Rejected','Failed','Skipped'))
    );

    CREATE INDEX [IX_AmazonEdi_Invoice_Status] ON [AmazonEdi].[Invoice] ([Status]) INCLUDE ([AttemptCount]);
    CREATE INDEX [IX_AmazonEdi_Invoice_InvoiceDate] ON [AmazonEdi].[Invoice] ([InvoiceDate] DESC);
END
GO

IF OBJECT_ID(N'[AmazonEdi].[InvoiceLine]', N'U') IS NULL
BEGIN
    -- A snapshot of exactly what we sent, so a later reconciliation against Amazon does not depend
    -- on Omni still holding the same figures.
    CREATE TABLE [AmazonEdi].[InvoiceLine]
    (
        [Id]                        BIGINT          IDENTITY(1,1) NOT NULL,
        [InvoiceId]                 BIGINT          NOT NULL,
        [LineNumber]                INT             NOT NULL,
        [StockCode]                 NVARCHAR(50)    NOT NULL,
        [Barcode]                   NVARCHAR(50)    NULL,
        [Asin]                      NVARCHAR(20)    NULL,
        [PurchaseOrderNumber]       NVARCHAR(50)    NULL,
        [Quantity]                  INT             NOT NULL,
        [UnitPriceExcludingTax]     DECIMAL(19,4)   NOT NULL,
        [LineTotalExcludingTax]     DECIMAL(19,4)   NOT NULL,
        [LineTax]                   DECIMAL(19,4)   NOT NULL,
        [TaxRate]                   DECIMAL(9,4)    NOT NULL,
        CONSTRAINT [PK_AmazonEdi_InvoiceLine] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_AmazonEdi_InvoiceLine_Invoice] FOREIGN KEY ([InvoiceId])
            REFERENCES [AmazonEdi].[Invoice] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [UQ_AmazonEdi_InvoiceLine] UNIQUE ([InvoiceId], [LineNumber])
    );
END
GO

IF OBJECT_ID(N'[AmazonEdi].[InvoiceAttempt]', N'U') IS NULL
BEGIN
    -- One row per API call, successful or not. Keeping the raw request and response is what makes
    -- "did Amazon ever actually receive this?" answerable months later.
    CREATE TABLE [AmazonEdi].[InvoiceAttempt]
    (
        [Id]                BIGINT          IDENTITY(1,1) NOT NULL,
        [InvoiceId]         BIGINT          NOT NULL,
        [AttemptNumber]     INT             NOT NULL,
        [HttpStatusCode]    INT             NOT NULL,
        [Succeeded]         BIT             NOT NULL,
        [TransactionId]     NVARCHAR(100)   NULL,
        [RequestJson]       NVARCHAR(MAX)   NOT NULL,
        [ResponseJson]      NVARCHAR(MAX)   NULL,
        [ErrorMessage]      NVARCHAR(MAX)   NULL,
        [CreatedAtUtc]      DATETIME2(3)    NOT NULL CONSTRAINT [DF_AmazonEdi_InvoiceAttempt_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_AmazonEdi_InvoiceAttempt] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_AmazonEdi_InvoiceAttempt_Invoice] FOREIGN KEY ([InvoiceId])
            REFERENCES [AmazonEdi].[Invoice] ([Id]) ON DELETE CASCADE
    );

    CREATE INDEX [IX_AmazonEdi_InvoiceAttempt_InvoiceId] ON [AmazonEdi].[InvoiceAttempt] ([InvoiceId]);
END
GO
