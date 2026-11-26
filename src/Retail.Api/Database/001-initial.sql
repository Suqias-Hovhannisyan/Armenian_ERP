SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Products (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
        Sku nvarchar(64) NOT NULL CONSTRAINT UQ_Products_Sku UNIQUE,
        Name nvarchar(200) NOT NULL,
        UnitPriceAmd bigint NOT NULL CONSTRAINT CK_Products_Price CHECK (UnitPriceAmd BETWEEN 1 AND 1000000000),
        Stock decimal(19, 3) NOT NULL CONSTRAINT CK_Products_Stock CHECK (Stock BETWEEN 0 AND 1000000000000)
    );

    CREATE TABLE dbo.Sales (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
        ProductId uniqueidentifier NOT NULL CONSTRAINT FK_Sales_Products REFERENCES dbo.Products(Id),
        Quantity decimal(19, 3) NOT NULL CONSTRAINT CK_Sales_Quantity CHECK (Quantity > 0),
        ReturnedQuantity decimal(19, 3) NOT NULL,
        UnitPriceAmd bigint NOT NULL,
        CONSTRAINT CK_Sales_Returned CHECK (ReturnedQuantity BETWEEN 0 AND Quantity)
    );

    CREATE TABLE dbo.StockMovements (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_StockMovements PRIMARY KEY,
        ProductId uniqueidentifier NOT NULL CONSTRAINT FK_StockMovements_Products REFERENCES dbo.Products(Id),
        SaleId uniqueidentifier NULL CONSTRAINT FK_StockMovements_Sales REFERENCES dbo.Sales(Id),
        Kind varchar(8) NOT NULL,
        QuantityDelta decimal(19, 3) NOT NULL,
        UnitPriceAmd bigint NULL,
        TotalAmd bigint NULL,
        OccurredAtUtc datetime2(3) NOT NULL CONSTRAINT DF_StockMovements_OccurredAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_StockMovements_Details CHECK (
            (Kind = 'receipt' AND QuantityDelta > 0 AND SaleId IS NULL AND UnitPriceAmd IS NULL AND TotalAmd IS NULL)
            OR (Kind = 'sale' AND QuantityDelta < 0 AND SaleId IS NOT NULL AND UnitPriceAmd IS NOT NULL AND TotalAmd IS NOT NULL)
            OR (Kind = 'return' AND QuantityDelta > 0 AND SaleId IS NOT NULL AND UnitPriceAmd IS NOT NULL AND TotalAmd IS NOT NULL)
        )
    );
    CREATE INDEX IX_StockMovements_Product_Date ON dbo.StockMovements(ProductId, OccurredAtUtc, Id);

    CREATE TABLE dbo.WriteRequests (
        RequestId uniqueidentifier NOT NULL CONSTRAINT PK_WriteRequests PRIMARY KEY,
        Fingerprint binary(32) NOT NULL,
        ResponseJson nvarchar(max) NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_WriteRequests_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;

COMMIT TRANSACTION;
