SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.Products', 'RequiresMarking') IS NULL
BEGIN
    ALTER TABLE dbo.Products ADD
        Barcode varchar(64) COLLATE Latin1_General_100_BIN2 NULL,
        Gtin varchar(14) COLLATE Latin1_General_100_BIN2 NULL,
        AtgCode varchar(10) NULL,
        RequiresMarking bit NOT NULL CONSTRAINT DF_Products_RequiresMarking DEFAULT (0);

    -- Preserve the printed barcode while normalizing retail GTIN widths for scanner lookup.
    EXEC(N'ALTER TABLE dbo.Products ADD BarcodeLookup AS (
        CASE WHEN LEN(Barcode) IN (8,12,13,14) AND Barcode NOT LIKE ''%[^0-9]%''
             THEN RIGHT(''00000000000000'' + Barcode,14) ELSE Barcode END
    ) PERSISTED;');
    EXEC(N'ALTER TABLE dbo.Products ADD
        CONSTRAINT CK_Products_Gtin CHECK (Gtin IS NULL OR LEN(Gtin) = 14 AND Gtin NOT LIKE ''%[^0-9]%''),
        CONSTRAINT CK_Products_AtgCode CHECK (AtgCode IS NULL OR LEN(AtgCode) = 10 AND AtgCode COLLATE Latin1_General_100_BIN2 NOT LIKE ''%[^0-9]%''),
        CONSTRAINT CK_Products_MarkingGtin CHECK (RequiresMarking = 0 OR Gtin IS NOT NULL);');
    EXEC(N'CREATE UNIQUE INDEX UX_Products_BarcodeLookup ON dbo.Products(BarcodeLookup) WHERE Barcode IS NOT NULL;');
    EXEC(N'CREATE UNIQUE INDEX UX_Products_Gtin ON dbo.Products(Gtin) WHERE Gtin IS NOT NULL;');
END;

COMMIT TRANSACTION;
