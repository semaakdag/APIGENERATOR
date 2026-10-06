-- Schema used to exercise endpoint recipes: client keys, IDENTITY keys and composite keys.
CREATE TABLE Products (
    Id INT NOT NULL PRIMARY KEY,
    Code NVARCHAR(20) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    IsActive BIT NOT NULL,
    CreatedAt DATETIME2 NOT NULL
);

CREATE TABLE Orders (
    OrderId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Number NVARCHAR(30) NOT NULL,
    PlacedOn DATE NOT NULL,
    IsOpen BIT NOT NULL
);

CREATE TABLE Stock (
    WarehouseId INT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL,
    CountedAt DATETIMEOFFSET NULL,
    CONSTRAINT PK_Stock PRIMARY KEY (WarehouseId, ProductId)
);
