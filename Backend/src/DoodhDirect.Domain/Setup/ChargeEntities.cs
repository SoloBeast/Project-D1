using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Setup;

/// <summary>
/// One configurable checkout charge (tax/fee) in the Setup → Tax &amp; Charges master.
/// Configuration data only: every <c>Active</c> record is applied to customer
/// purchases; inactive records are ignored. Applicability is configuration only:
/// <c>ApplicableOnAll</c> charges are global, while <c>ApplicableOnAll = false</c>
/// charges may be assigned to individual products via <see cref="ProductCharge"/>.
/// Never hard-code business charge names here — the code/description are
/// admin-maintained.
/// </summary>
public sealed class Charge : AuditableEntity
{
    private Charge() { }

    public Charge(
        string chargeType,
        string chargeCode,
        string? description,
        decimal percentage,
        bool applicableOnAll = true)
    {
        ChargeType = ChargeValidation.Type(chargeType);
        ChargeCode = ChargeValidation.Code(chargeCode);
        Description = ChargeValidation.Description(description);
        Percentage = ChargeValidation.Percentage(percentage, nameof(percentage));
        ApplicableOnAll = applicableOnAll;
        IsActive = true;
    }

    public string ChargeType { get; private set; } = string.Empty;
    public string ChargeCode { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Percentage { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>
    /// True → global charge (applies to every eligible purchase; MUST NOT carry any
    /// <see cref="ProductCharge"/> mapping). False → item charge, assignable to
    /// products. Defaults to true so legacy charges keep their global behavior.
    /// </summary>
    public bool ApplicableOnAll { get; private set; } = true;

    /// <summary>True once any order snapshot references this code (drives edit/delete guards).</summary>
    public bool IsUsed { get; private set; }

    public void Update(string chargeType, string? description, decimal percentage)
    {
        ChargeType = ChargeValidation.Type(chargeType);
        Description = ChargeValidation.Description(description);
        Percentage = ChargeValidation.Percentage(percentage, nameof(percentage));
    }

    public void MarkUsed() => IsUsed = true;

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Switches the applicability mode. Callers MUST enforce the invariant:
    /// enabling <see cref="ApplicableOnAll"/> is only allowed when zero
    /// <see cref="ProductCharge"/> mappings reference this charge (checked by
    /// <c>ChargeService</c> inside its transaction).
    /// </summary>
    public void SetApplicability(bool applicableOnAll) => ApplicableOnAll = applicableOnAll;
}

/// <summary>
/// Junction row assigning an item-level charge (<see cref="Charge.ApplicableOnAll"/>
/// = false) to a product. Configuration only — never a pricing snapshot. Unique per
/// (ProductId, ChargeId); a charge may be shared by many products and a product may
/// carry many charges.
/// </summary>
public sealed class ProductCharge : Entity
{
    private ProductCharge() { }

    public ProductCharge(long productId, long chargeId)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (chargeId <= 0) throw new ArgumentOutOfRangeException(nameof(chargeId));

        ProductId = productId;
        ChargeId = chargeId;
    }

    public long ProductId { get; private set; }
    public long ChargeId { get; private set; }

    public Product Product { get; private set; } = null!;
    public Charge Charge { get; private set; } = null!;
}

/// <summary>
/// Frozen per-order copy of one charge applied at checkout. Historical orders must
/// never recalculate from the current master — every display value lives here.
/// No navigation/FK to <see cref="Charge"/>: an order references the charge by code.
/// </summary>
public sealed class OrderCharge : Entity
{
    private OrderCharge() { }

    public OrderCharge(
        string chargeType,
        string chargeCode,
        string? description,
        decimal percentage,
        decimal baseAmount,
        decimal amount)
    {
        ChargeType = ChargeValidation.Type(chargeType);
        ChargeCode = ChargeValidation.Code(chargeCode);
        Description = ChargeValidation.Description(description);
        if (percentage <= 0 || decimal.Round(percentage, 2) != percentage)
        {
            throw new ArgumentException("Percentage must be positive with at most two decimal places.", nameof(percentage));
        }

        if (baseAmount < 0 || decimal.Round(baseAmount, 2) != baseAmount)
        {
            throw new ArgumentException("Base amount must be a non-negative two-decimal value.", nameof(baseAmount));
        }

        if (amount < 0 || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("Amount must be a non-negative two-decimal value.", nameof(amount));
        }

        Percentage = percentage;
        BaseAmount = baseAmount;
        Amount = amount;
    }

    public long OrderId { get; private set; }
    public string ChargeType { get; private set; } = string.Empty;
    public string ChargeCode { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Percentage { get; private set; }
    /// <summary>The taxable base this charge was computed from (auditability).</summary>
    public decimal BaseAmount { get; private set; }
    /// <summary>Rounded charge amount; rows of an order sum exactly to Order.ChargesTotal.</summary>
    public decimal Amount { get; private set; }

    public Order Order { get; private set; } = null!;
}

/// <summary>
/// Frozen per-subscription copy of one charge applied at creation. Historical
/// subscriptions must never recalculate from the current master — every display
/// value lives here. No navigation/FK to <see cref="Charge"/>: a subscription
/// references the charge by code, mirroring <see cref="OrderCharge"/>.
/// </summary>
public sealed class SubscriptionCharge : Entity
{
    private SubscriptionCharge() { }

    public SubscriptionCharge(
        string chargeType,
        string chargeCode,
        string? description,
        decimal percentage,
        decimal baseAmount,
        decimal amount)
    {
        ChargeType = ChargeValidation.Type(chargeType);
        ChargeCode = ChargeValidation.Code(chargeCode);
        Description = ChargeValidation.Description(description);
        if (percentage <= 0 || decimal.Round(percentage, 2) != percentage)
        {
            throw new ArgumentException("Percentage must be positive with at most two decimal places.", nameof(percentage));
        }

        if (baseAmount < 0 || decimal.Round(baseAmount, 2) != baseAmount)
        {
            throw new ArgumentException("Base amount must be a non-negative two-decimal value.", nameof(baseAmount));
        }

        if (amount < 0 || decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("Amount must be a non-negative two-decimal value.", nameof(amount));
        }

        Percentage = percentage;
        BaseAmount = baseAmount;
        Amount = amount;
    }

    public long SubscriptionId { get; private set; }
    public string ChargeType { get; private set; } = string.Empty;
    public string ChargeCode { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Percentage { get; private set; }
    /// <summary>The prepaid subscription product value this charge was computed from (auditability).</summary>
    public decimal BaseAmount { get; private set; }
    /// <summary>Rounded charge amount; rows of a subscription sum exactly to Subscription.ChargesTotal.</summary>
    public decimal Amount { get; private set; }

    public Subscription Subscription { get; private set; } = null!;
}

internal static class ChargeValidation
{
    internal static string Type(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40)
        {
            throw new ArgumentException("Charge type is required and must be at most 40 characters.");
        }

        return trimmed;
    }

    internal static string Code(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 20)
        {
            throw new ArgumentException("Charge code is required and must be at most 20 characters.");
        }

        return trimmed;
    }

    internal static string? Description(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > 200)
        {
            throw new ArgumentException("Description must be at most 200 characters.");
        }

        return trimmed;
    }

    internal static decimal Percentage(decimal value, string paramName)
    {
        if (value <= 0 || value > 100)
        {
            throw new ArgumentException("Percentage must be greater than 0 and at most 100.", paramName);
        }

        if (decimal.Round(value, 2) != value)
        {
            throw new ArgumentException("Percentage supports at most two decimal places.", paramName);
        }

        return value;
    }
}
