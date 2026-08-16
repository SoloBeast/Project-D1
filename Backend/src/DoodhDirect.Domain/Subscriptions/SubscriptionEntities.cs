using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Subscriptions;

public enum SubscriptionStatus
{
    PaymentPending,
    Active,
    Paused,
    Completed,
    Cancelled,
    PaymentFailed
}

public enum SubscriptionDeliveryStatus
{
    Scheduled,
    Skipped,
    Failed,
    Delivered,
    Cancelled
}

public sealed class Subscription : AuditableEntity
{
    private Subscription() { }

    public Subscription(
        long customerId,
        long productId,
        long customerAddressId,
        long branchId,
        string idempotencyKey,
        DateOnly startDate,
        DateOnly endDate,
        decimal quantity,
        decimal unitPrice,
        int totalEntitlement,
        string productSku,
        string productName,
        string unitOfMeasure,
        string branchCode,
        string branchName,
        string addressSnapshot)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (customerAddressId <= 0) throw new ArgumentOutOfRangeException(nameof(customerAddressId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (startDate > endDate) throw new ArgumentException("The subscription end date cannot precede its start date.", nameof(endDate));
        if (quantity <= 0 || decimal.Round(quantity, 3) != quantity)
        {
            throw new ArgumentException("Quantity must be positive and use at most three decimal places.", nameof(quantity));
        }
        if (unitPrice <= 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        if (totalEntitlement <= 0) throw new ArgumentOutOfRangeException(nameof(totalEntitlement));

        CustomerId = customerId;
        ProductId = productId;
        CustomerAddressId = customerAddressId;
        BranchId = branchId;
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        Status = SubscriptionStatus.PaymentPending;
        StartDate = startDate;
        EndDate = endDate;
        Quantity = quantity;
        UnitPrice = decimal.Round(unitPrice, 2, MidpointRounding.AwayFromZero);
        TotalEntitlement = totalEntitlement;
        PayableAmount = decimal.Round(Quantity * UnitPrice * TotalEntitlement, 2, MidpointRounding.AwayFromZero);
        ProductSkuSnapshot = Required(productSku, nameof(productSku));
        ProductNameSnapshot = Required(productName, nameof(productName));
        UnitOfMeasureSnapshot = Required(unitOfMeasure, nameof(unitOfMeasure)).ToLowerInvariant();
        BranchCodeSnapshot = Required(branchCode, nameof(branchCode));
        BranchNameSnapshot = Required(branchName, nameof(branchName));
        AddressSnapshot = Required(addressSnapshot, nameof(addressSnapshot));
    }

    public long CustomerId { get; private set; }
    public long ProductId { get; private set; }
    public long CustomerAddressId { get; private set; }
    public long BranchId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public SubscriptionStatus Status { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal PayableAmount { get; private set; }
    public int TotalEntitlement { get; private set; }
    public int UsedEntitlement { get; private set; }
    public int RemainingEntitlement => TotalEntitlement - UsedEntitlement;
    public string ProductSkuSnapshot { get; private set; } = string.Empty;
    public string ProductNameSnapshot { get; private set; } = string.Empty;
    public string UnitOfMeasureSnapshot { get; private set; } = string.Empty;
    public string BranchCodeSnapshot { get; private set; } = string.Empty;
    public string BranchNameSnapshot { get; private set; } = string.Empty;
    public string AddressSnapshot { get; private set; } = string.Empty;
    public DateTime? ActivatedAtUtc { get; private set; }
    public DateTime? PausedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public User Customer { get; private set; } = null!;
    public Product Product { get; private set; } = null!;
    public CustomerAddress CustomerAddress { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;
    public ICollection<SubscriptionSchedule> Schedules { get; private set; } = [];
    public ICollection<SubscriptionDelivery> Deliveries { get; private set; } = [];

    public void AddSchedule(DayOfWeek dayOfWeek)
    {
        if (Schedules.Any(x => x.DayOfWeek == dayOfWeek))
        {
            throw new InvalidOperationException($"The schedule already includes {dayOfWeek}.");
        }

        Schedules.Add(new SubscriptionSchedule(dayOfWeek));
    }

    public void AddDelivery(DateOnly scheduledDate)
    {
        if (scheduledDate < StartDate || scheduledDate > EndDate)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduledDate));
        }
        if (Deliveries.Any(x => x.ScheduledDate == scheduledDate))
        {
            throw new InvalidOperationException("A delivery already exists for this date.");
        }

        Deliveries.Add(new SubscriptionDelivery(
            scheduledDate,
            BranchId,
            Quantity,
            BranchCodeSnapshot,
            BranchNameSnapshot,
            AddressSnapshot));
    }

    public void Activate(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionStatus.Active) return;
        if (Status != SubscriptionStatus.PaymentPending)
        {
            throw new InvalidOperationException($"A subscription in status '{Status}' cannot be activated by payment.");
        }

        Status = SubscriptionStatus.Active;
        ActivatedAtUtc = utcNow;
    }

    public void FailPayment()
    {
        if (Status == SubscriptionStatus.PaymentFailed) return;
        if (Status != SubscriptionStatus.PaymentPending)
        {
            throw new InvalidOperationException($"A subscription in status '{Status}' cannot be marked as payment failed.");
        }

        Status = SubscriptionStatus.PaymentFailed;
    }

    public void Pause(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionStatus.Paused) return;
        if (Status != SubscriptionStatus.Active)
        {
            throw new InvalidOperationException($"A subscription in status '{Status}' cannot be paused.");
        }

        Status = SubscriptionStatus.Paused;
        PausedAtUtc = utcNow;
    }

    public void Resume()
    {
        if (Status == SubscriptionStatus.Active) return;
        if (Status != SubscriptionStatus.Paused)
        {
            throw new InvalidOperationException($"A subscription in status '{Status}' cannot be resumed.");
        }

        Status = SubscriptionStatus.Active;
        PausedAtUtc = null;
    }

    public void Cancel(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionStatus.Cancelled) return;
        if (Status is SubscriptionStatus.Completed or SubscriptionStatus.PaymentFailed)
        {
            throw new InvalidOperationException($"A subscription in status '{Status}' cannot be cancelled.");
        }

        Status = SubscriptionStatus.Cancelled;
        CancelledAtUtc = utcNow;
        foreach (var delivery in Deliveries.Where(x => x.Status == SubscriptionDeliveryStatus.Scheduled))
        {
            delivery.Cancel(utcNow);
        }
    }

    public void Skip(SubscriptionDelivery delivery, DateTime utcNow, TimeSpan cutoff)
    {
        EnsureOwned(delivery);
        if (Status is not (SubscriptionStatus.Active or SubscriptionStatus.Paused))
        {
            throw new InvalidOperationException($"A delivery cannot be skipped while the subscription is '{Status}'.");
        }

        delivery.Skip(utcNow, cutoff);
    }

    public void MarkFailed(SubscriptionDelivery delivery, DateTime utcNow)
    {
        EnsureOwned(delivery);
        delivery.Fail(utcNow);
    }

    public void MarkDelivered(SubscriptionDelivery delivery, DateTime utcNow)
    {
        EnsureOwned(delivery);
        if (!delivery.Deliver(utcNow)) return;
        if (UsedEntitlement >= TotalEntitlement)
        {
            throw new InvalidOperationException("The prepaid entitlement is exhausted.");
        }

        UsedEntitlement++;
        if (UsedEntitlement == TotalEntitlement)
        {
            Status = SubscriptionStatus.Completed;
            CompletedAtUtc = utcNow;
        }
    }

    private void EnsureOwned(SubscriptionDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!Deliveries.Contains(delivery))
        {
            throw new InvalidOperationException("The delivery does not belong to this subscription.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public sealed class SubscriptionSchedule : Entity
{
    private SubscriptionSchedule() { }

    internal SubscriptionSchedule(DayOfWeek dayOfWeek)
    {
        DayOfWeek = dayOfWeek;
    }

    public long SubscriptionId { get; private set; }
    public DayOfWeek DayOfWeek { get; private set; }
    public Subscription Subscription { get; private set; } = null!;
}

public sealed class SubscriptionDelivery : PublicEntity
{
    private SubscriptionDelivery() { }

    internal SubscriptionDelivery(
        DateOnly scheduledDate,
        long branchId,
        decimal quantity,
        string branchCode,
        string branchName,
        string addressSnapshot)
    {
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        ScheduledDate = scheduledDate;
        BranchId = branchId;
        Quantity = quantity;
        Status = SubscriptionDeliveryStatus.Scheduled;
        BranchCodeSnapshot = Required(branchCode, nameof(branchCode));
        BranchNameSnapshot = Required(branchName, nameof(branchName));
        AddressSnapshot = Required(addressSnapshot, nameof(addressSnapshot));
    }

    public long SubscriptionId { get; private set; }
    public long BranchId { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public decimal Quantity { get; private set; }
    public SubscriptionDeliveryStatus Status { get; private set; }
    public string BranchCodeSnapshot { get; private set; } = string.Empty;
    public string BranchNameSnapshot { get; private set; } = string.Empty;
    public string AddressSnapshot { get; private set; } = string.Empty;
    public DateTime? StatusChangedAtUtc { get; private set; }

    public Subscription Subscription { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;

    internal void Skip(DateTime utcNow, TimeSpan cutoff)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionDeliveryStatus.Skipped) return;
        EnsureScheduled();

        var deliveryStartsUtc = DateTime.SpecifyKind(ScheduledDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        if (utcNow > deliveryStartsUtc - cutoff)
        {
            throw new InvalidOperationException("The skip cutoff has passed for this delivery.");
        }

        Status = SubscriptionDeliveryStatus.Skipped;
        StatusChangedAtUtc = utcNow;
    }

    internal void Fail(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionDeliveryStatus.Failed) return;
        EnsureScheduled();
        Status = SubscriptionDeliveryStatus.Failed;
        StatusChangedAtUtc = utcNow;
    }

    internal bool Deliver(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionDeliveryStatus.Delivered) return false;
        EnsureScheduled();
        Status = SubscriptionDeliveryStatus.Delivered;
        StatusChangedAtUtc = utcNow;
        return true;
    }

    internal void Cancel(DateTime utcNow)
    {
        EnsureUtc(utcNow, nameof(utcNow));
        if (Status == SubscriptionDeliveryStatus.Cancelled) return;
        EnsureScheduled();
        Status = SubscriptionDeliveryStatus.Cancelled;
        StatusChangedAtUtc = utcNow;
    }

    private void EnsureScheduled()
    {
        if (Status != SubscriptionDeliveryStatus.Scheduled)
        {
            throw new InvalidOperationException($"A delivery in status '{Status}' cannot change terminal state.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}
