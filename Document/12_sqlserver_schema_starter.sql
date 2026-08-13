/* DoodhDirect SQL Server 2025 starter schema.
   This is a foundation script. Production migrations should be generated and managed through EF Core.
*/

CREATE TABLE dbo.Branch (
    BranchId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Branch PRIMARY KEY,
    PublicId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_Branch_PublicId UNIQUE DEFAULT NEWSEQUENTIALID(),
    BranchCode NVARCHAR(50) NOT NULL CONSTRAINT UQ_Branch_BranchCode UNIQUE,
    BranchName NVARCHAR(200) NOT NULL,
    AddressLine1 NVARCHAR(300) NULL,
    AddressLine2 NVARCHAR(300) NULL,
    Locality NVARCHAR(150) NULL,
    City NVARCHAR(100) NOT NULL,
    State NVARCHAR(100) NOT NULL,
    PinCode NVARCHAR(10) NULL,
    Latitude DECIMAL(9,6) NOT NULL,
    Longitude DECIMAL(9,6) NOT NULL,
    ServiceRadiusKm DECIMAL(8,2) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Branch_IsActive DEFAULT 1,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Branch_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Branch_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.[User] (
    UserId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_User PRIMARY KEY,
    PublicId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_User_PublicId UNIQUE DEFAULT NEWSEQUENTIALID(),
    UserType NVARCHAR(30) NOT NULL,
    Mobile NVARCHAR(20) NULL,
    Email NVARCHAR(320) NULL,
    PasswordHash NVARCHAR(500) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_User_IsActive DEFAULT 1,
    LastLoginAtUtc DATETIME2(3) NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_User_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_User_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
);

CREATE UNIQUE INDEX UX_User_Mobile ON dbo.[User](Mobile) WHERE Mobile IS NOT NULL;
CREATE UNIQUE INDEX UX_User_Email ON dbo.[User](Email) WHERE Email IS NOT NULL;

CREATE TABLE dbo.Customer (
    CustomerId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    UserId BIGINT NOT NULL CONSTRAINT UQ_Customer_UserId UNIQUE,
    CustomerNumber NVARCHAR(50) NOT NULL CONSTRAINT UQ_Customer_Number UNIQUE,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NULL,
    Status NVARCHAR(30) NOT NULL,
    ReferralCode NVARCHAR(50) NULL CONSTRAINT UQ_Customer_ReferralCode UNIQUE,
    ReferredByCustomerId BIGINT NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Customer_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Customer_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Customer_User FOREIGN KEY (UserId) REFERENCES dbo.[User](UserId),
    CONSTRAINT FK_Customer_ReferredBy FOREIGN KEY (ReferredByCustomerId) REFERENCES dbo.Customer(CustomerId)
);

CREATE TABLE dbo.CustomerAddress (
    AddressId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerAddress PRIMARY KEY,
    CustomerId BIGINT NOT NULL,
    AddressLabel NVARCHAR(50) NOT NULL,
    AddressLine1 NVARCHAR(300) NOT NULL,
    AddressLine2 NVARCHAR(300) NULL,
    Locality NVARCHAR(150) NULL,
    City NVARCHAR(100) NOT NULL,
    State NVARCHAR(100) NOT NULL,
    PinCode NVARCHAR(10) NULL,
    Landmark NVARCHAR(200) NULL,
    Latitude DECIMAL(9,6) NOT NULL,
    Longitude DECIMAL(9,6) NOT NULL,
    ContactName NVARCHAR(200) NULL,
    ContactMobile NVARCHAR(20) NULL,
    DeliveryInstructions NVARCHAR(1000) NULL,
    IsDefault BIT NOT NULL CONSTRAINT DF_CustomerAddress_IsDefault DEFAULT 0,
    IsActive BIT NOT NULL CONSTRAINT DF_CustomerAddress_IsActive DEFAULT 1,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_CustomerAddress_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_CustomerAddress_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_CustomerAddress_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer(CustomerId)
);

CREATE INDEX IX_CustomerAddress_CustomerId ON dbo.CustomerAddress(CustomerId);

CREATE TABLE dbo.Product (
    ProductId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Product PRIMARY KEY,
    PublicId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_Product_PublicId UNIQUE DEFAULT NEWSEQUENTIALID(),
    SKU NVARCHAR(50) NOT NULL CONSTRAINT UQ_Product_SKU UNIQUE,
    ProductName NVARCHAR(200) NOT NULL,
    Description NVARCHAR(2000) NULL,
    Category NVARCHAR(100) NOT NULL,
    UnitOfMeasure NVARCHAR(20) NOT NULL,
    Price DECIMAL(18,2) NOT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Product_IsActive DEFAULT 1,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Product_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Product_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.ProductBranch (
    ProductBranchId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductBranch PRIMARY KEY,
    ProductId BIGINT NOT NULL,
    BranchId BIGINT NOT NULL,
    IsAvailable BIT NOT NULL CONSTRAINT DF_ProductBranch_IsAvailable DEFAULT 1,
    MaxDailyQuantity DECIMAL(18,3) NULL,
    CONSTRAINT FK_ProductBranch_Product FOREIGN KEY (ProductId) REFERENCES dbo.Product(ProductId),
    CONSTRAINT FK_ProductBranch_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(BranchId),
    CONSTRAINT UQ_ProductBranch UNIQUE(ProductId, BranchId)
);

CREATE TABLE dbo.Orders (
    OrderId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    PublicOrderId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_Orders_PublicOrderId UNIQUE DEFAULT NEWSEQUENTIALID(),
    CustomerId BIGINT NOT NULL,
    BranchId BIGINT NOT NULL,
    DeliveryAddressId BIGINT NOT NULL,
    OrderType NVARCHAR(30) NOT NULL,
    OrderStatus NVARCHAR(40) NOT NULL,
    OrderDateUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Orders_OrderDateUtc DEFAULT SYSUTCDATETIME(),
    Subtotal DECIMAL(18,2) NOT NULL,
    Discount DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_Discount DEFAULT 0,
    Tax DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_Tax DEFAULT 0,
    DeliveryCharge DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_DeliveryCharge DEFAULT 0,
    WalletAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_WalletAmount DEFAULT 0,
    PayableAmount DECIMAL(18,2) NOT NULL,
    PaymentStatus NVARCHAR(30) NOT NULL,
    AssignedEmployeeId BIGINT NULL,
    DeliveredAtUtc DATETIME2(3) NULL,
    FailedAtUtc DATETIME2(3) NULL,
    FailureReason NVARCHAR(500) NULL,
    ComplaintEligibleUntilUtc DATETIME2(3) NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Orders_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Orders_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Orders_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer(CustomerId),
    CONSTRAINT FK_Orders_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(BranchId),
    CONSTRAINT FK_Orders_Address FOREIGN KEY (DeliveryAddressId) REFERENCES dbo.CustomerAddress(AddressId)
);

CREATE INDEX IX_Orders_Customer_Date ON dbo.Orders(CustomerId, OrderDateUtc DESC);
CREATE INDEX IX_Orders_Branch_Status_Date ON dbo.Orders(BranchId, OrderStatus, OrderDateUtc);

CREATE TABLE dbo.OrderItem (
    OrderItemId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderItem PRIMARY KEY,
    OrderId BIGINT NOT NULL,
    ProductId BIGINT NOT NULL,
    Quantity DECIMAL(18,3) NOT NULL,
    UnitOfMeasure NVARCHAR(20) NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    Discount DECIMAL(18,2) NOT NULL CONSTRAINT DF_OrderItem_Discount DEFAULT 0,
    Tax DECIMAL(18,2) NOT NULL CONSTRAINT DF_OrderItem_Tax DEFAULT 0,
    LineAmount DECIMAL(18,2) NOT NULL,
    CONSTRAINT FK_OrderItem_Order FOREIGN KEY (OrderId) REFERENCES dbo.Orders(OrderId),
    CONSTRAINT FK_OrderItem_Product FOREIGN KEY (ProductId) REFERENCES dbo.Product(ProductId)
);

CREATE TABLE dbo.Delivery (
    DeliveryId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Delivery PRIMARY KEY,
    PublicId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_Delivery_PublicId UNIQUE DEFAULT NEWSEQUENTIALID(),
    OrderId BIGINT NOT NULL CONSTRAINT UQ_Delivery_OrderId UNIQUE,
    EmployeeId BIGINT NULL,
    Status NVARCHAR(40) NOT NULL,
    AssignedAtUtc DATETIME2(3) NULL,
    PickedUpAtUtc DATETIME2(3) NULL,
    StartedAtUtc DATETIME2(3) NULL,
    ArrivedAtUtc DATETIME2(3) NULL,
    CompletedAtUtc DATETIME2(3) NULL,
    FailureReason NVARCHAR(500) NULL,
    DeliveryLatitude DECIMAL(9,6) NULL,
    DeliveryLongitude DECIMAL(9,6) NULL,
    OTPVerified BIT NOT NULL CONSTRAINT DF_Delivery_OTPVerified DEFAULT 0,
    CustomerRejected BIT NOT NULL CONSTRAINT DF_Delivery_CustomerRejected DEFAULT 0,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Delivery_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Delivery_UpdatedAtUtc DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Delivery_Employee_Status ON dbo.Delivery(EmployeeId, Status);

CREATE TABLE dbo.MilkProduction (
    ProductionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MilkProduction PRIMARY KEY,
    BranchId BIGINT NOT NULL,
    ProductionDate DATE NOT NULL,
    Shift NVARCHAR(30) NULL,
    TotalBuffaloCount INT NOT NULL,
    QuantityProduced DECIMAL(18,3) NOT NULL,
    UnitOfMeasure NVARCHAR(20) NOT NULL,
    RecordedBy BIGINT NULL,
    RecordedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_MilkProduction_RecordedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_MilkProduction_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(BranchId)
);

CREATE TABLE dbo.MilkBatch (
    MilkBatchId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MilkBatch PRIMARY KEY,
    PublicBatchId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_MilkBatch_PublicBatchId UNIQUE DEFAULT NEWSEQUENTIALID(),
    BranchId BIGINT NOT NULL,
    BatchNumber NVARCHAR(80) NOT NULL CONSTRAINT UQ_MilkBatch_BatchNumber UNIQUE,
    ProductionId BIGINT NOT NULL,
    ProductionDate DATE NOT NULL,
    StartTimeUtc DATETIME2(3) NOT NULL,
    EndTimeUtc DATETIME2(3) NULL,
    QuantityProduced DECIMAL(18,3) NOT NULL,
    AvailableQuantity DECIMAL(18,3) NOT NULL,
    Status NVARCHAR(30) NOT NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_MilkBatch_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_MilkBatch_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(BranchId),
    CONSTRAINT FK_MilkBatch_Production FOREIGN KEY (ProductionId) REFERENCES dbo.MilkProduction(ProductionId)
);

CREATE INDEX IX_MilkBatch_Branch_Date ON dbo.MilkBatch(BranchId, ProductionDate DESC);

CREATE TABLE dbo.Wallet (
    WalletId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Wallet PRIMARY KEY,
    CustomerId BIGINT NOT NULL CONSTRAINT UQ_Wallet_Customer UNIQUE,
    CurrentBalance DECIMAL(18,2) NOT NULL CONSTRAINT DF_Wallet_CurrentBalance DEFAULT 0,
    Currency CHAR(3) NOT NULL CONSTRAINT DF_Wallet_Currency DEFAULT 'INR',
    Status NVARCHAR(20) NOT NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Wallet_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Wallet_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Wallet_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer(CustomerId)
);

CREATE TABLE dbo.WalletTransaction (
    WalletTransactionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WalletTransaction PRIMARY KEY,
    WalletId BIGINT NOT NULL,
    TransactionType NVARCHAR(40) NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    Direction NVARCHAR(10) NOT NULL,
    ReferenceType NVARCHAR(50) NULL,
    ReferenceId NVARCHAR(100) NULL,
    BalanceBefore DECIMAL(18,2) NOT NULL,
    BalanceAfter DECIMAL(18,2) NOT NULL,
    Remarks NVARCHAR(500) NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_WalletTransaction_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CreatedBy BIGINT NULL,
    CONSTRAINT FK_WalletTransaction_Wallet FOREIGN KEY (WalletId) REFERENCES dbo.Wallet(WalletId)
);

CREATE INDEX IX_WalletTransaction_Wallet_Date ON dbo.WalletTransaction(WalletId, CreatedAtUtc DESC);

CREATE TABLE dbo.Complaint (
    ComplaintId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Complaint PRIMARY KEY,
    PublicComplaintId UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_Complaint_PublicComplaintId UNIQUE DEFAULT NEWSEQUENTIALID(),
    CustomerId BIGINT NOT NULL,
    OrderId BIGINT NOT NULL,
    CategoryCode NVARCHAR(50) NOT NULL,
    Description NVARCHAR(2000) NULL,
    Status NVARCHAR(40) NOT NULL,
    IsWithinWindow BIT NOT NULL,
    SubmittedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Complaint_SubmittedAtUtc DEFAULT SYSUTCDATETIME(),
    EligibleUntilUtc DATETIME2(3) NULL,
    ReviewedAtUtc DATETIME2(3) NULL,
    ReviewedBy BIGINT NULL,
    ResolutionType NVARCHAR(30) NULL,
    ResolutionRemarks NVARCHAR(2000) NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Complaint_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_Complaint_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Complaint_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer(CustomerId),
    CONSTRAINT FK_Complaint_Order FOREIGN KEY (OrderId) REFERENCES dbo.Orders(OrderId)
);

CREATE INDEX IX_Complaint_Customer_Date ON dbo.Complaint(CustomerId, SubmittedAtUtc DESC);
CREATE INDEX IX_Complaint_Order ON dbo.Complaint(OrderId);

CREATE TABLE dbo.AuditLog (
    AuditLogId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    UserId BIGINT NULL,
    Action NVARCHAR(100) NOT NULL,
    EntityType NVARCHAR(100) NOT NULL,
    EntityId NVARCHAR(100) NOT NULL,
    OldValueJson NVARCHAR(MAX) NULL,
    NewValueJson NVARCHAR(MAX) NULL,
    IPAddress NVARCHAR(64) NULL,
    UserAgent NVARCHAR(1000) NULL,
    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_AuditLog_CreatedAtUtc DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_AuditLog_Entity ON dbo.AuditLog(EntityType, EntityId, CreatedAtUtc DESC);
