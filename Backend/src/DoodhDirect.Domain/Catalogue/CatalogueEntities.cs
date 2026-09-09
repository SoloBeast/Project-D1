using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Catalogue;

public sealed class ProductCategory : AuditableEntity
{
    private ProductCategory() { }

    public ProductCategory(string code, string name, string? description = null)
    {
        Update(code, name, description);
        IsActive = false;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; } = true;

    public ICollection<Product> Products { get; private set; } = [];

    public void Update(string code, string name, string? description)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        Description = Normalize(description);
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class Product : AuditableEntity
{
    private Product() { }

    public Product(long categoryId, string sku, string name, string? description, string unitOfMeasure, decimal price)
    {
        CategoryId = categoryId;
        Update(categoryId, sku, name, description, unitOfMeasure, price);
        IsActive = false;
    }

    public long CategoryId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string UnitOfMeasure { get; private set; } = string.Empty;
    public decimal Price { get; private set; }
    public bool IsActive { get; private set; } = true;

    public ProductCategory Category { get; private set; } = null!;
    public ICollection<ProductBranch> ProductBranches { get; private set; } = [];

    public void Update(long categoryId, string sku, string name, string? description, string unitOfMeasure, decimal price)
    {
        CategoryId = categoryId;
        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Description = Normalize(description);
        UnitOfMeasure = unitOfMeasure.Trim().ToLowerInvariant();
        Price = price;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class Branch : AuditableEntity
{
    private Branch() { }

    public Branch(string code, string name, string city, string state, decimal latitude, decimal longitude)
    {
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        City = city.Trim();
        State = state.Trim();
        Latitude = latitude;
        Longitude = longitude;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? AddressLine1 { get; private set; }
    public string? AddressLine2 { get; private set; }
    public string? Locality { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string? PinCode { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public decimal? ServiceRadiusKm { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAt { get; private set; }

    public ICollection<ProductBranch> ProductBranches { get; private set; } = [];

    public void Update(
        string name,
        string? addressLine1,
        string? addressLine2,
        string? locality,
        string city,
        string state,
        string? pinCode,
        decimal latitude,
        decimal longitude,
        decimal? serviceRadiusKm)
    {
        Name = name.Trim();
        AddressLine1 = Normalize(addressLine1);
        AddressLine2 = Normalize(addressLine2);
        Locality = Normalize(locality);
        City = city.Trim();
        State = state.Trim();
        PinCode = Normalize(pinCode);
        Latitude = latitude;
        Longitude = longitude;
        ServiceRadiusKm = serviceRadiusKm;
    }

    /// <summary>
    /// Changes the branch code. Only allowed when the branch is not yet referenced
    /// by orders, product availability, or a scoped ORDER numbering series; the
    /// service enforces those guards before calling this method.
    /// </summary>
    public void ChangeCode(string code)
    {
        Code = code.Trim().ToUpperInvariant();
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Archives the branch. An archived branch is retained for historical and reporting
    /// data but is excluded from normal branch lists/selectors and cannot receive new
    /// business activity. Archiving always leaves the branch deactivated.
    /// </summary>
    public void Archive(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        IsActive = false;
        IsArchived = true;
        ArchivedAt = indiaLocalNow;
    }

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Catalogue timestamps must be India-local DateTime values with an unspecified kind.",
                parameterName);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProductBranch : Entity
{
    private ProductBranch() { }

    public ProductBranch(long productId, long branchId, bool isAvailable, decimal? maxDailyQuantity)
    {
        ProductId = productId;
        BranchId = branchId;
        Update(isAvailable, maxDailyQuantity);
    }

    public long ProductId { get; private set; }
    public long BranchId { get; private set; }
    public bool IsAvailable { get; private set; }
    public decimal? MaxDailyQuantity { get; private set; }

    public Product Product { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;

    public void Update(bool isAvailable, decimal? maxDailyQuantity)
    {
        IsAvailable = isAvailable;
        MaxDailyQuantity = maxDailyQuantity;
    }
}
