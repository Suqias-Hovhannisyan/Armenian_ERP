SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.PosReceipts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PosReceipts (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PosReceipts PRIMARY KEY,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_PosReceipts_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID(N'dbo.PosReceiptLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PosReceiptLines (
        ReceiptId uniqueidentifier NOT NULL REFERENCES dbo.PosReceipts(Id),
        LineNumber int NOT NULL CHECK (LineNumber > 0),
        SaleId uniqueidentifier NOT NULL REFERENCES dbo.Sales(Id),
        CONSTRAINT PK_PosReceiptLines PRIMARY KEY (ReceiptId, LineNumber),
        CONSTRAINT UQ_PosReceiptLines_Sale UNIQUE (SaleId)
    );
END;

COMMIT TRANSACTION;
