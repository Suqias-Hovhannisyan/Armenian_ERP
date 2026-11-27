SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.MarkedUnits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MarkedUnits (
        Gtin varchar(14) NOT NULL,
        Serial varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ProductId uniqueidentifier NOT NULL REFERENCES dbo.Products(Id),
        RawCode varchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CurrentSaleId uniqueidentifier NULL REFERENCES dbo.Sales(Id),
        CONSTRAINT PK_MarkedUnits PRIMARY KEY (Gtin, Serial)
    );
    CREATE INDEX IX_MarkedUnits_Product ON dbo.MarkedUnits(ProductId);
END;

IF OBJECT_ID(N'dbo.MarkedUnitMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MarkedUnitMovements (
        MovementId uniqueidentifier NOT NULL REFERENCES dbo.StockMovements(Id),
        Gtin varchar(14) NOT NULL,
        Serial varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CONSTRAINT PK_MarkedUnitMovements PRIMARY KEY (MovementId, Gtin, Serial),
        CONSTRAINT FK_MarkedUnitMovements_Unit FOREIGN KEY (Gtin, Serial) REFERENCES dbo.MarkedUnits(Gtin, Serial)
    );
END;

COMMIT TRANSACTION;
