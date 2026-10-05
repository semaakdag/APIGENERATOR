-- Edge cases for the SQL parser and generators
/* block comment with CREATE TABLE Fake (Id INT) inside */
CREATE TABLE [dbo].[Customer Orders] (
    [OrderId] BIGINT IDENTITY(1,1) NOT NULL,
    [CustomerCode] VARCHAR(20) NOT NULL,
    [Amount] DECIMAL(18, 4) NULL,
    [IsPaid] BIT NOT NULL DEFAULT 0,
    [OrderedAt] DATETIME2(7) NOT NULL,
    [ShippedOn] DATE NULL,
    [ExternalId] UNIQUEIDENTIFIER NOT NULL,
    [Notes] NVARCHAR(MAX) NULL,
    [RowVersion] ROWVERSION,
    CONSTRAINT [PK_CustomerOrders] PRIMARY KEY CLUSTERED ([OrderId] ASC)
);
GO

create table sales.order_line (
    order_line_id int not null primary key,
    order_id bigint not null references [dbo].[Customer Orders](OrderId),
    product_name nvarchar(200) not null,
    quantity smallint not null,
    unit_price money not null,
    discount float null,
    ratio real null,
    flags tinyint null,
    payload varbinary(max) null,
    created time null,
    offset_at datetimeoffset null,
    "class" nvarchar(10) null,
    [event] int null
);

CREATE TABLE AuditLog
(
    LogKey UNIQUEIDENTIFIER PRIMARY KEY,
    Message NVARCHAR(4000) NOT NULL,
    Level CHAR(1) NOT NULL
)

CREATE TABLE NoPrimaryKey (
    Code NVARCHAR(10) NOT NULL,
    Description NVARCHAR(100) NULL
);

CREATE TABLE CompositeKey (
    TenantId INT NOT NULL,
    ItemId INT NOT NULL,
    Name NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_CompositeKey PRIMARY KEY (TenantId, ItemId)
);
