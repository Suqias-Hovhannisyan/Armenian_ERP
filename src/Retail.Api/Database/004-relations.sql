SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.ProductCustomFields', N'TargetModelKey') IS NULL
BEGIN
    ALTER TABLE dbo.ProductCustomFields ADD TargetModelKey varchar(64) NULL;
    ALTER TABLE dbo.ProductCustomFields DROP CONSTRAINT CK_ProductCustomFields_Type;

    -- Compile these constraints after TargetModelKey exists on an upgraded phase-one database.
    EXEC(N'ALTER TABLE dbo.ProductCustomFields ADD
        CONSTRAINT FK_ProductCustomFields_Target FOREIGN KEY (TargetModelKey) REFERENCES dbo.CustomModels(ModelKey),
        CONSTRAINT UQ_ProductCustomFields_Target UNIQUE (FieldKey, TargetModelKey),
        CONSTRAINT CK_ProductCustomFields_Type CHECK (
            (FieldType IN (''text'', ''integer'', ''boolean'') AND TargetModelKey IS NULL)
            OR (FieldType = ''reference'' AND TargetModelKey IS NOT NULL)
        )');
END;

IF OBJECT_ID(N'dbo.ProductReferenceValues', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductReferenceValues (
        ProductId uniqueidentifier NOT NULL,
        FieldKey varchar(64) NOT NULL,
        ModelKey varchar(64) NOT NULL,
        RecordId uniqueidentifier NOT NULL,
        CONSTRAINT PK_ProductReferenceValues PRIMARY KEY (ProductId, FieldKey),
        CONSTRAINT FK_ProductReferenceValues_Product FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
        CONSTRAINT FK_ProductReferenceValues_Field FOREIGN KEY (FieldKey, ModelKey)
            REFERENCES dbo.ProductCustomFields(FieldKey, TargetModelKey),
        CONSTRAINT FK_ProductReferenceValues_Record FOREIGN KEY (ModelKey, RecordId)
            REFERENCES dbo.CustomModelRecords(ModelKey, Id)
    );
END;

COMMIT TRANSACTION;
