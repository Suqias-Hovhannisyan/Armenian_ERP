SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.CustomModels', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomModels (
        ModelKey varchar(64) NOT NULL CONSTRAINT PK_CustomModels PRIMARY KEY,
        Label nvarchar(200) NOT NULL,
        CONSTRAINT CK_CustomModels_Key CHECK (
            LEN(ModelKey) BETWEEN 1 AND 64 AND ModelKey COLLATE Latin1_General_100_BIN2 LIKE '[a-z]%'
            AND ModelKey COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9_]%'
            AND ModelKey <> 'products'
        ),
        CONSTRAINT CK_CustomModels_Label CHECK (LEN(LTRIM(Label)) > 0)
    );
END;

IF OBJECT_ID(N'dbo.CustomModelRecords', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomModelRecords (
        ModelKey varchar(64) NOT NULL,
        Id uniqueidentifier NOT NULL,
        Name nvarchar(200) NOT NULL,
        CONSTRAINT PK_CustomModelRecords PRIMARY KEY (ModelKey, Id),
        CONSTRAINT FK_CustomModelRecords_Model FOREIGN KEY (ModelKey) REFERENCES dbo.CustomModels(ModelKey),
        CONSTRAINT UQ_CustomModelRecords_Name UNIQUE (ModelKey, Name),
        CONSTRAINT CK_CustomModelRecords_Name CHECK (LEN(LTRIM(Name)) > 0)
    );
END;

COMMIT TRANSACTION;
