SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.ProductEditorPermissions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductEditorPermissions (
        Id tinyint NOT NULL CONSTRAINT PK_ProductEditorPermissions PRIMARY KEY,
        ManagerCanEdit bit NOT NULL,
        CONSTRAINT CK_ProductEditorPermissions_Singleton CHECK (Id = 1)
    );

    INSERT INTO dbo.ProductEditorPermissions(Id, ManagerCanEdit) VALUES (1, 0);
END;

IF OBJECT_ID(N'dbo.ProductCustomFields', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductCustomFields (
        FieldKey varchar(64) NOT NULL CONSTRAINT PK_ProductCustomFields PRIMARY KEY,
        Label nvarchar(200) NOT NULL,
        FieldType varchar(12) NOT NULL,
        FormPosition int NULL,
        ManagerCanUse bit NOT NULL DEFAULT (0),
        CONSTRAINT CK_ProductCustomFields_Type CHECK (FieldType IN ('text', 'integer', 'boolean')),
        CONSTRAINT CK_ProductCustomFields_Position CHECK (FormPosition BETWEEN 0 AND 10000)
    );
END;

IF OBJECT_ID(N'dbo.ProductCustomValues', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductCustomValues (
        ProductId uniqueidentifier NOT NULL,
        FieldKey varchar(64) NOT NULL,
        ValueJson nvarchar(max) NOT NULL,
        CONSTRAINT PK_ProductCustomValues PRIMARY KEY (ProductId, FieldKey),
        CONSTRAINT FK_ProductCustomValues_Product FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
        CONSTRAINT FK_ProductCustomValues_Field FOREIGN KEY (FieldKey) REFERENCES dbo.ProductCustomFields(FieldKey)
    );
END;

COMMIT TRANSACTION;
