using System.Data;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Orders;

public sealed class BranchAllocationService(DoodhDirectDbContext dbContext) : IBranchAllocationService
{
    public async Task<BranchAllocationResult> AllocateAsync(
        decimal latitude,
        decimal longitude,
        IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
        CancellationToken cancellationToken)
    {
        ValidateCoordinates(latitude, longitude);

        var productIds = items.Select(item => item.ProductId).Distinct().ToArray();
        var candidates = await dbContext.Branches
            .AsNoTracking()
            .Where(branch => branch.IsActive)
            .Include(branch => branch.ProductBranches)
            .Where(branch => branch.ProductBranches.Count(link =>
                link.IsAvailable &&
                productIds.Contains(link.ProductId) &&
                link.Product.IsActive) == productIds.Length)
            .ToListAsync(cancellationToken);

        // Geographic coverage is determined independently of cart fulfillment:
        // inside means at least one active branch has an unlimited (NULL)
        // radius or a configured radius containing the delivery coordinates.
        // A NULL radius keeps its existing unlimited-radius semantics.
        var coverage = await dbContext.Branches
            .AsNoTracking()
            .Where(branch => branch.IsActive)
            .Select(branch => new
            {
                branch.Latitude,
                branch.Longitude,
                branch.ServiceRadiusKm
            })
            .ToListAsync(cancellationToken);
        var isInsideAnyBranchRadius = coverage.Any(branch =>
            !branch.ServiceRadiusKm.HasValue ||
            DistanceKm(latitude, longitude, branch.Latitude, branch.Longitude) <= branch.ServiceRadiusKm.Value);

        if (!isInsideAnyBranchRadius)
        {
            await RejectOutsideRadiusIneligibleItemsAsync(productIds, cancellationToken);
        }

        var eligible = candidates
            .Select(branch => new
            {
                Branch = branch,
                DistanceKm = DistanceKm(latitude, longitude, branch.Latitude, branch.Longitude)
            })
            // Inside a radius, only in-radius branches may fulfil the cart: a
            // nearby branch that cannot fulfil the whole cart never falls back
            // to an outside-radius branch for non-opted-in products.
            .Where(candidate => !isInsideAnyBranchRadius ||
                               !candidate.Branch.ServiceRadiusKm.HasValue ||
                               candidate.DistanceKm <= candidate.Branch.ServiceRadiusKm.Value)
            .Where(candidate => candidate.Branch.ProductBranches
                .Where(link => productIds.Contains(link.ProductId))
                .All(link => !link.MaxDailyQuantity.HasValue ||
                             items.Single(item => item.ProductId == link.ProductId).Quantity <= link.MaxDailyQuantity.Value))
            .OrderBy(candidate => candidate.DistanceKm)
            .ThenBy(candidate => candidate.Branch.Id)
            .FirstOrDefault();

        if (eligible is null)
        {
            throw new BusinessRuleException("No active branch can fulfil the requested products for this address.");
        }

        return new BranchAllocationResult(
            eligible.Branch.Id,
            eligible.Branch.PublicId,
            eligible.Branch.Code,
            eligible.Branch.Name,
            decimal.Round(eligible.DistanceKm, 3));
    }

    /// <summary>
    /// Outside every branch radius, every cart product must explicitly allow
    /// outside-radius ordering. Rejects the whole cart naming each offending
    /// SKU; lines are never silently dropped and quantities never changed.
    /// </summary>
    private async Task RejectOutsideRadiusIneligibleItemsAsync(
        long[] productIds,
        CancellationToken cancellationToken)
    {
        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Sku, product.AllowOrderOutsideBranchRadius })
            .ToListAsync(cancellationToken);
        var ineligible = products
            .Where(product => !product.AllowOrderOutsideBranchRadius)
            .Select(product => product.Sku)
            .OrderBy(sku => sku, StringComparer.Ordinal)
            .ToArray();
        if (ineligible.Length > 0)
        {
            throw new ValidationAppException(
                "The delivery address is outside every branch service radius, so the following products cannot be ordered: " +
                string.Join(", ", ineligible) +
                ". Remove them from the cart or choose a different delivery address.",
                "items");
        }
    }

    private static void ValidateCoordinates(decimal latitude, decimal longitude)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ValidationAppException("Address coordinates are invalid.");
        }
    }

    private static decimal DistanceKm(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        const double earthRadiusKm = 6371.0088;
        var lat1 = double.DegreesToRadians((double)latitude1);
        var lat2 = double.DegreesToRadians((double)latitude2);
        var deltaLat = lat2 - lat1;
        var deltaLon = double.DegreesToRadians((double)(longitude2 - longitude1));
        var a = Math.Pow(Math.Sin(deltaLat / 2), 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(deltaLon / 2), 2);
        var distance = earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (decimal)distance;
    }
}

public sealed class OrderService(
    DoodhDirectDbContext dbContext,
    IBranchAllocationService branchAllocationService,
    INotificationEventWriter notificationEventWriter,
    INumberSeriesService numberSeriesService,
    IIndiaTimeProvider timeProvider,
    IRefundReplacementConfigurationService refundConfigurationService) : IOrderService
{
    public async Task<CheckoutResult> PreviewAsync(long customerId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        var calculation = await CalculateAsync(customerId, request, cancellationToken);
        var (charges, _) = await ResolveChargesAsync(calculation, track: false, cancellationToken);
        return calculation.ToResult(charges);
    }

    public async Task<OrderResult> CreateAsync(
        long customerId,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new ValidationAppException("A valid Idempotency-Key header is required.", "Idempotency-Key");
        }

        var existing = await dbContext.Orders
            .Include(order => order.Items)
            .Include(order => order.Branch)
            .Include(order => order.CustomerAddress)
            .Include(order => order.Items).ThenInclude(item => item.Product)
            .SingleOrDefaultAsync(order => order.CustomerId == customerId && order.IdempotencyKey == idempotencyKey.Trim(), cancellationToken);
        if (existing is not null)
        {
            return await ToResultAsync(existing, cancellationToken);
        }

        var calculation = await CalculateAsync(customerId, request, cancellationToken);
        string? orderNumber = null;
        string? notificationEventKey = null;
        List<string>? managerNotificationEventKeys = null;
        Order? order = null;
        try
        {
            await ExecuteSerializableAsync(async () =>
            {
                orderNumber = await numberSeriesService.GetNextNumberAsync(
                    "ORDER",
                    customerId,
                    cancellationToken,
                    calculation.Allocation.BranchCode);
                order = new Order(
                    customerId,
                    calculation.Address.Id,
                    calculation.Allocation.BranchId,
                    idempotencyKey.Trim(),
                    orderNumber,
                    calculation.Subtotal,
                    calculation.DiscountAmount,
                    calculation.Allocation.BranchCode,
                    calculation.Allocation.BranchName,
                    calculation.Address.Label,
                    calculation.Address.AddressLine1,
                    calculation.Address.AddressLine2,
                    calculation.Address.Locality,
                    calculation.Address.City,
                    calculation.Address.State,
                    calculation.Address.PinCode,
                    calculation.Address.Landmark,
                    calculation.Address.DeliveryInstructions,
                    calculation.Address.ContactName,
                    calculation.Address.ContactMobile,
                    calculation.Address.Latitude,
                    calculation.Address.Longitude);

                foreach (var line in calculation.Lines)
                {
                    order.AddItem(new OrderItem(
                        line.Product.Id,
                        line.Quantity,
                        line.Product.Price,
                        line.Product.Sku,
                        line.Product.Name,
                        line.Product.UnitOfMeasure));
                }

                // Active charges are read INSIDE the serializable transaction so an
                // admin edit either lands fully before or fully after this checkout —
                // the order always freezes one consistent set. Each amount is rounded
                // independently (approved rule); PayableAmount becomes charge-inclusive.
                var (charges, chargeMasters) = await ResolveChargesAsync(
                    calculation,
                    track: true,
                    cancellationToken);
                order.AddCharges(charges.Lines
                    .Select(line => new OrderCharge(
                        line.ChargeType,
                        line.ChargeCode,
                        line.Description,
                        line.Percentage,
                        line.BaseAmount,
                        line.Amount))
                    .ToArray());
                foreach (var master in chargeMasters)
                {
                    if (!master.IsUsed)
                    {
                        master.MarkUsed();
                    }
                }

                dbContext.Orders.Add(order);
                notificationEventKey = $"order:{order.PublicId:N}:created";
                notificationEventWriter.Add(new NotificationEventRequest(
                    customerId,
                    NotificationEventTypes.OrderCreated,
                    notificationEventKey,
                    new Dictionary<string, string>
                    {
                        ["message"] = $"Order {order.OrderNumber} has been created."
                    },
                    $"/orders/{order.PublicId}"));
                managerNotificationEventKeys = await AddManagerOrderPlacedEventsAsync(
                    calculation.Allocation.BranchId,
                    order,
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (order is not null)
            {
                dbContext.Entry(order).State = EntityState.Detached;
                foreach (var item in order.Items)
                {
                    dbContext.Entry(item).State = EntityState.Detached;
                }

                foreach (var charge in order.Charges)
                {
                    dbContext.Entry(charge).State = EntityState.Detached;
                }
            }

            if (notificationEventKey is not null)
            {
                var notificationEvent = dbContext.NotificationEvents.Local
                    .SingleOrDefault(item => item.EventKey == notificationEventKey);
                if (notificationEvent is not null)
                {
                    dbContext.Entry(notificationEvent).State = EntityState.Detached;
                }
            }

            if (managerNotificationEventKeys is not null)
            {
                foreach (var managerKey in managerNotificationEventKeys)
                {
                    var managerEvent = dbContext.NotificationEvents.Local
                        .SingleOrDefault(item => item.EventKey == managerKey);
                    if (managerEvent is not null)
                    {
                        dbContext.Entry(managerEvent).State = EntityState.Detached;
                    }
                }
            }

            var duplicate = await LoadOrderAsync(customerId, idempotencyKey.Trim(), cancellationToken);
            if (duplicate is not null) return await ToResultAsync(duplicate, cancellationToken);
            throw;
        }

        await LoadNavigationAsync(order!, cancellationToken);
        return await ToResultAsync(order!, cancellationToken);
    }

    /// <summary>
    /// Notifies the active Dairy Manager and Delivery Manager recipients for the
    /// allocated branch when a one-time order is successfully placed. Recipients
    /// are resolved from persisted roles, active users/roles, and branch
    /// assignments — never from client input — and scoped to the authoritative
    /// allocated branch (branch-specific or global assignments only). One
    /// deterministic event per recipient keeps retried order creation free of
    /// duplicate notifications; a single employee holding both roles receives
    /// only one copy.
    /// </summary>
    private async Task<List<string>> AddManagerOrderPlacedEventsAsync(
        long branchId,
        Order order,
        CancellationToken cancellationToken)
    {
        var staffRoleCodes = new[]
        {
            AuthorizationCodes.DairyManager,
            AuthorizationCodes.DeliveryManager
        };
        var recipientIds = await dbContext.UserRoles
            .AsNoTracking()
            .Where(x => (x.BranchId == branchId || x.BranchId == null)
                && staffRoleCodes.Contains(x.Role.Code)
                && x.Role.IsActive
                && x.User.IsActive)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var keys = new List<string>(recipientIds.Count);
        foreach (var userId in recipientIds)
        {
            var eventKey = $"order:{order.PublicId:N}:placed:staff:{userId}";
            keys.Add(eventKey);
            notificationEventWriter.Add(new NotificationEventRequest(
                userId,
                NotificationEventTypes.OrderCreated,
                eventKey,
                new Dictionary<string, string>
                {
                    ["orderId"] = order.PublicId.ToString(),
                    ["orderNumber"] = order.OrderNumber,
                    ["branchCode"] = order.BranchCodeSnapshot,
                    ["branchName"] = order.BranchNameSnapshot,
                    ["message"] = $"New order {order.OrderNumber} for {order.BranchNameSnapshot}."
                },
                $"/staff/orders/{order.PublicId}",
                timeProvider.Now));
        }

        return keys;
    }

    public async Task<IReadOnlyList<OrderResult>> GetForCustomerAsync(long customerId, CancellationToken cancellationToken) =>
        await ToResultsAsync(
            await QueryOrders()
                .Where(order => order.CustomerId == customerId)
                .OrderByDescending(order => order.CreatedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<OrderResult>> GetForAdministrationAsync(CancellationToken cancellationToken) =>
        await ToResultsAsync(
            await QueryOrders()
                .OrderByDescending(order => order.CreatedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<OrderResult> GetForBranchAsync(
        OrderActor actor,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(
                order => order.PublicId == orderId &&
                    (actor.HasGlobalAccess || actor.BranchIds.Contains(order.BranchId)),
                cancellationToken);

        return order is null
            ? throw new NotFoundException("Order was not found.")
            : await ToResultAsync(order, cancellationToken);
    }

    public async Task<OrderResult> GetAsync(long customerId, Guid orderId, bool bypassOwnership, CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(order => order.PublicId == orderId && (bypassOwnership || order.CustomerId == customerId), cancellationToken);
        return order is null
            ? throw new NotFoundException("Order was not found.")
            : await ToResultAsync(order, cancellationToken);
    }

    /// <summary>
    /// Server-backed cancellation preview: authoritative pickup state, the
    /// live Admin Centre deduction percentage, and the exact refund estimate
    /// the final endpoint would settle. Read-only: never mutates, never
    /// moves money, never requires a reason. The confirm endpoint
    /// recalculates everything; the preview is a reference only.
    /// </summary>
    public async Task<CancelOrderPreviewResult> PreviewCancelAsync(long customerId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(order => order.PublicId == orderId && order.CustomerId == customerId, cancellationToken)
            ?? throw new NotFoundException("Order was not found.");
        var existingCancellation = await dbContext.OrderCancellations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, cancellationToken);
        if (existingCancellation is not null)
        {
            return new CancelOrderPreviewResult(false, "The order is already cancelled.", null, false, 0m, order.PayableAmount, 0m, 0m, 0m, false);
        }

        var deliveries = await dbContext.Deliveries
            .AsNoTracking()
            .Where(delivery => delivery.OrderId == order.Id)
            .ToListAsync(cancellationToken);
        var pickup = ClassifyPickup(order, deliveries);
        if (pickup.BlockReason is not null)
        {
            return new CancelOrderPreviewResult(false, pickup.BlockReason, pickup.DeliveryStatus, pickup.PickupOccurred, 0m, order.PayableAmount, 0m, 0m, 0m, false);
        }

        var payments = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == order.Id && payment.CustomerId == customerId)
            .OrderBy(payment => payment.Id)
            .ToListAsync(cancellationToken);
        var paymentBlock = ClassifyPayments(payments);
        if (paymentBlock is not null)
        {
            return new CancelOrderPreviewResult(false, paymentBlock, pickup.DeliveryStatus, pickup.PickupOccurred, 0m, order.PayableAmount, 0m, 0m, 0m, false);
        }

        var deductionPercentage = pickup.PickupOccurred
            ? (await refundConfigurationService.GetAsync(cancellationToken)).DeliveryFailDeductionPercentage
            : 0m;
        var settlement = SettleAmounts(order.PayableAmount, deductionPercentage, payments);
        return new CancelOrderPreviewResult(
            true, null, pickup.DeliveryStatus, pickup.PickupOccurred,
            deductionPercentage, settlement.GrossBasis, settlement.DeductionAmount,
            settlement.RefundTotal, settlement.EligiblePaid, pickup.PickupOccurred);
    }

    public async Task<OrderResult> CancelAsync(long customerId, Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var (reasonCode, comment) = ValidateCancelRequest(request);
        try
        {
            OrderResult? result = null;
            await ExecuteSerializableAsync(async () =>
            {
                result = await CancelCoreAsync(customerId, orderId, request, reasonCode, comment, cancellationToken);
            }, cancellationToken);
            return result!;
        }
        catch (DbUpdateException)
        {
            // A concurrent winner committed first (unique OrderCancellation
            // row, wallet/refund idempotency keys): adopt its persisted
            // outcome instead of duplicating money movement.
            dbContext.ChangeTracker.Clear();
            var current = await QueryOrders()
                .SingleOrDefaultAsync(x => x.PublicId == orderId && x.CustomerId == customerId, cancellationToken)
                ?? throw new NotFoundException("Order was not found.");
            return await ToResultAsync(current, cancellationToken);
        }
    }

    private async Task<OrderResult> CancelCoreAsync(
        long customerId,
        Guid orderId,
        CancelOrderRequest request,
        string reasonCode,
        string? comment,
        CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(order => order.PublicId == orderId && order.CustomerId == customerId, cancellationToken)
            ?? throw new NotFoundException("Order was not found.");
        var now = timeProvider.Now;

        var existingCancellation = await dbContext.OrderCancellations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, cancellationToken);
        if (existingCancellation is not null)
        {
            return await ToResultAsync(order, cancellationToken);
        }

        var deliveries = await dbContext.Deliveries
            .Include(delivery => delivery.Otps)
            .Where(delivery => delivery.OrderId == order.Id)
            .ToListAsync(cancellationToken);
        var pickup = ClassifyPickup(order, deliveries);
        if (pickup.BlockReason is not null)
        {
            throw new BusinessRuleException(pickup.BlockReason);
        }

        var payments = await dbContext.Payments
            .Include(payment => payment.Refunds)
            .Where(payment => payment.OrderId == order.Id && payment.CustomerId == customerId)
            .OrderBy(payment => payment.Id)
            .ToListAsync(cancellationToken);
        var paymentBlock = ClassifyPayments(payments);
        if (paymentBlock is not null)
        {
            throw new ConflictException(paymentBlock);
        }

        // Overlapping refund/replacement claims: an unresolved ticket owns
        // the delivery's financial outcome, and an already-settled portion
        // must never be refunded again. Cancellation creates no extra
        // failed-delivery ticket to implement itself.
        if (deliveries.Count > 0)
        {
            var deliveryIds = deliveries.Select(delivery => delivery.Id).ToList();
            var overlapping = await dbContext.RefundReplacementRequests
                .AsNoTracking()
                .Where(ticket => deliveryIds.Contains(ticket.DeliveryId))
                .ToListAsync(cancellationToken);
            if (overlapping.Any(ticket => ticket.Status is RefundReplacementStatus.Pending
                or RefundReplacementStatus.Approved
                or RefundReplacementStatus.AwaitingCustomerChoice))
            {
                throw new ConflictException(
                    "The order cannot be cancelled yet: an unresolved refund claim exists for its delivery. Resolve it with support first.");
            }
        }
        if (await OneTimeOrderRefund.HasVerifiedSettlementAsync(dbContext, order.Id, customerId, cancellationToken))
        {
            throw new ConflictException(
                "The order cannot be cancelled with a refund: a part of its paid amount was already refunded. Contact support.");
        }

        var deductionPercentage = pickup.PickupOccurred
            ? (await refundConfigurationService.GetAsync(cancellationToken)).DeliveryFailDeductionPercentage
            : 0m;
        var settlement = SettleAmounts(order.PayableAmount, deductionPercentage, payments);

        if (pickup.PickupOccurred)
        {
            // O3: client confirmation is UX only — the server revalidates the
            // live terms and rejects a stale or forged confirmation instead
            // of repricing silently.
            if (!request.Confirmed)
            {
                throw new BusinessRuleException(
                    "Cancelling after pickup requires explicit confirmation of the shown deduction and refund.");
            }
            if (request.ConfirmedDeductionPercentage != deductionPercentage
                || request.ConfirmedRefundAmount != settlement.RefundTotal)
            {
                throw new ConflictException(
                    "The cancellation terms changed since the preview. Refresh the preview and confirm again.");
            }
        }

        try
        {
            order.Cancel(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessRuleException(exception.Message);
        }

        var cancelReason = $"Order cancelled by customer ({reasonCode}).";
        foreach (var delivery in deliveries)
        {
            try
            {
                if (delivery.Status is DeliveryStatus.PickedUp or DeliveryStatus.OutForDelivery or DeliveryStatus.Arrived)
                {
                    delivery.CancelAfterPickup(now, cancelReason);
                }
                else if (delivery.Status is DeliveryStatus.ReadyForAssignment or DeliveryStatus.Assigned)
                {
                    delivery.Cancel(now, cancelReason);
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new BusinessRuleException(exception.Message);
            }
            foreach (var otp in delivery.Otps)
            {
                otp.Invalidate(now);
            }
        }

        var cancellation = new OrderCancellation(
            order.Id,
            reasonCode,
            comment,
            customerId,
            now);
        cancellation.RecordRefundSnapshot(
            deductionPercentage, settlement.DeductionAmount, settlement.RefundTotal, settlement.GrossBasis);
        dbContext.OrderCancellations.Add(cancellation);

        if (settlement.RefundTotal > 0)
        {
            var allocations = OneTimeOrderRefund.Allocate(settlement.RefundTotal, payments);
            var walletKey = CancelWalletKey(order.PublicId);
            var currency = payments.Count > 0 ? payments[0].Currency : "INR";
            var firstPaymentId = allocations.Count > 0 ? allocations[0].Payment.Id : (long?)null;
            await OneTimeOrderRefund.CreditWalletAsync(
                dbContext, customerId, order.Id, firstPaymentId, currency,
                settlement.RefundTotal, walletKey,
                pickup.PickupOccurred
                    ? $"Order-cancellation refund for {order.OrderNumber} after pickup (deduction {deductionPercentage:0.##}% retained)."
                    : $"Order-cancellation full refund for {order.OrderNumber} before pickup.",
                now, cancellationToken);
            foreach (var allocation in allocations)
            {
                RecordCancelRefund(
                    allocation.Payment, allocation.Amount,
                    CancelPaymentKey(order.PublicId, allocation.Payment.Id),
                    customerId, now);
            }

            dbContext.AddAuditLog(new Domain.Auditing.AuditLog(
                customerId,
                "ORDER.CANCEL_REFUND",
                "Order",
                order.PublicId.ToString(),
                System.Text.Json.JsonSerializer.Serialize(new { Status = order.Status.ToString() }),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Status = order.Status.ToString(),
                    ReasonCode = reasonCode,
                    HasComment = comment is not null,
                    PickupOccurred = pickup.PickupOccurred,
                    GrossBasis = settlement.GrossBasis,
                    DeductionPercentage = deductionPercentage,
                    DeductionAmount = settlement.DeductionAmount,
                    RefundTotal = settlement.RefundTotal,
                    EligiblePaid = settlement.EligiblePaid
                }),
                null,
                null,
                comment ?? reasonCode,
                now));
            notificationEventWriter.Add(new NotificationEventRequest(
                customerId,
                NotificationEventTypes.OrderCancelRefunded,
                CancelEventKey(order.PublicId),
                new Dictionary<string, string>
                {
                    ["orderId"] = order.PublicId.ToString(),
                    ["orderNumber"] = order.OrderNumber,
                    ["grossBasis"] = settlement.GrossBasis.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    ["deductionPercentage"] = deductionPercentage.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    ["deductionAmount"] = settlement.DeductionAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    ["amount"] = settlement.RefundTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    ["message"] = pickup.PickupOccurred
                        ? $"Your order {order.OrderNumber} was cancelled after pickup. {settlement.RefundTotal:0.00} was credited to your wallet."
                        : $"Your order {order.OrderNumber} was cancelled before pickup. {settlement.RefundTotal:0.00} was credited to your wallet."
                },
                $"/orders/{order.PublicId}",
                now));
        }
        else
        {
            dbContext.AddAuditLog(new Domain.Auditing.AuditLog(
                customerId,
                "ORDER.CANCEL",
                "Order",
                order.PublicId.ToString(),
                System.Text.Json.JsonSerializer.Serialize(new { Status = "Confirmed" }),
                System.Text.Json.JsonSerializer.Serialize(new { Status = order.Status.ToString(), ReasonCode = reasonCode, HasComment = comment is not null, RefundTotal = 0m }),
                null,
                null,
                comment ?? reasonCode,
                now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToResultAsync(order, cancellationToken);
    }

    private sealed record PickupClassification(bool PickupOccurred, string? DeliveryStatus, string? BlockReason);

    private static PickupClassification ClassifyPickup(Order order, IReadOnlyCollection<Delivery> deliveries)
    {
        if (deliveries.Count == 0)
        {
            return new PickupClassification(false, null, null);
        }

        var pickupOccurred = false;
        string? status = null;
        foreach (var delivery in deliveries)
        {
            status ??= delivery.Status.ToString();
            switch (delivery.Status)
            {
                case DeliveryStatus.Delivered:
                    return new PickupClassification(false, delivery.Status.ToString(),
                        $"Order {order.OrderNumber} has already been delivered and cannot be cancelled. Contact support for help.");
                case DeliveryStatus.Failed:
                    return new PickupClassification(false, delivery.Status.ToString(),
                        $"Order {order.OrderNumber} already has a failed delivery. Raise a refund request instead of cancelling.");
                case DeliveryStatus.PickedUp:
                case DeliveryStatus.OutForDelivery:
                case DeliveryStatus.Arrived:
                    pickupOccurred = true;
                    break;
            }
        }

        return new PickupClassification(pickupOccurred, status, null);
    }

    private static string? ClassifyPayments(IReadOnlyList<Payment> payments)
    {
        if (payments.Any(payment => payment.Status is PaymentStatus.Initiated or PaymentStatus.Pending))
        {
            return "The order cannot be cancelled yet: a payment attempt is still awaiting gateway confirmation. Wait for it to succeed, expire, or resolve it before cancelling.";
        }
        if (payments.Any(payment => payment.Status == PaymentStatus.RefundPending))
        {
            return "The order cannot be cancelled yet: another refund is already in progress. Wait for it to settle before cancelling.";
        }

        return null;
    }

    private sealed record CancelSettlement(decimal GrossBasis, decimal DeductionAmount, decimal RefundTotal, decimal EligiblePaid);

    private static CancelSettlement SettleAmounts(decimal payableAmount, decimal deductionPercentage, IReadOnlyList<Payment> payments)
    {
        var gross = decimal.Round(payableAmount, 2, MidpointRounding.AwayFromZero);
        var deduction = decimal.Round(gross * deductionPercentage / 100m, 2, MidpointRounding.AwayFromZero);
        var net = gross - deduction;
        if (net < 0) net = 0;
        var eligiblePaid = payments
            .Where(payment => payment.Status is PaymentStatus.Success or PaymentStatus.PartiallyRefunded)
            .Sum(payment => payment.Amount - payment.RefundedAmount);
        var refund = net > eligiblePaid ? eligiblePaid : net;
        return new CancelSettlement(gross, deduction, refund, eligiblePaid);
    }

    internal static string CancelWalletKey(Guid orderPublicId) =>
        $"order-cancel:{orderPublicId:N}:wallet";

    internal static string CancelPaymentKey(Guid orderPublicId, long paymentId) =>
        $"order-cancel:{orderPublicId:N}:payment:{paymentId}";

    internal static string CancelEventKey(Guid orderPublicId) =>
        $"order-cancel:{orderPublicId:N}:refunded";

    private static void RecordCancelRefund(
        Payment payment,
        decimal amount,
        string idempotencyKey,
        long customerId,
        DateTime now)
    {
        // Same local refund recording as the one-time failed-delivery path:
        // no gateway call, the money returns as wallet credit, and the
        // domain (Amount - RefundedAmount) cap is authoritative. Committed
        // rows with these keys are reused, never duplicated.
        var existing = payment.Refunds.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            if (existing.Status != RefundStatus.Succeeded)
            {
                throw new ConflictException(
                    "A refund with the same reference is already in progress.");
            }

            return;
        }

        Refund refund;
        try
        {
            refund = payment.StartRefund(amount, "Order-cancellation refund to wallet.", idempotencyKey, customerId);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
        try
        {
            refund.Succeed(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
        try
        {
            payment.CompleteRefund(amount);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
    }

    private static (string ReasonCode, string? Comment) ValidateCancelRequest(CancelOrderRequest? request)
    {
        if (request is null)
        {
            throw new ValidationAppException("A cancellation reason or comment is required.", "reasonCode");
        }

        var reason = string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode.Trim();
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is not null && comment.Length > OrderCancellation.MaxCommentLength)
        {
            throw new ValidationAppException($"Cancellation comment cannot exceed {OrderCancellation.MaxCommentLength} characters.", "comment");
        }

        if (reason is null && comment is null)
        {
            throw new ValidationAppException("A cancellation reason or comment is required.", "reasonCode");
        }

        if (reason is not null)
        {
            try
            {
                reason = OrderCancellation.NormalizeReason(reason);
            }
            catch (ArgumentException exception)
            {
                throw new ValidationAppException(exception.Message, exception.ParamName ?? "reasonCode");
            }
        }

        try
        {
            if (reason is null)
            {
                if (comment is null)
                {
                    throw new ValidationAppException("A cancellation reason or comment is required.", "reasonCode");
                }
            }
            else
            {
                comment = OrderCancellation.NormalizeComment(comment, reason);
            }
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName ?? "comment");
        }

        if (reason is null)
        {
            reason = nameof(OrderCancellationReason.Other);
        }

        return (reason, comment);
    }

    private async Task<Calculation> CalculateAsync(long customerId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (customerId <= 0) throw new UnauthorizedAppException();
        if (request is null) throw new ValidationAppException("Checkout details are required.");
        if ((request.AddressId.HasValue && request.AddressId.Value == Guid.Empty) ||
            (request.AddressId.HasValue == request.ManualAddress is not null))
        {
            throw new ValidationAppException(
                "Provide exactly one saved address or manual address.",
                "AddressId");
        }

        try
        {
            OrderValidation.ValidateItems(request.Items);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }

        var address = request.ManualAddress is not null
            ? DeliveryAddress.FromManual(request.ManualAddress)
            : await dbContext.CustomerAddresses
                .AsNoTracking()
                .Where(item => item.PublicId == request.AddressId && item.UserId == customerId && item.IsActive)
                .Select(item => new DeliveryAddress(
                    item.Id,
                    item.PublicId,
                    item.Label,
                    item.AddressLine1,
                    item.AddressLine2,
                    item.Locality,
                    item.City,
                    item.State,
                    item.PinCode,
                    item.Landmark,
                    item.DeliveryInstructions,
                    item.ContactName,
                    item.ContactMobile,
                    item.Latitude,
                    item.Longitude))
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("The selected active address was not found for this customer.");

        var productIds = request.Items.Select(item => item.ProductId).ToArray();
        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive && product.Category.IsActive && productIds.Contains(product.PublicId))
            .ToListAsync(cancellationToken);
        if (products.Count != productIds.Length)
        {
            throw new BusinessRuleException("One or more selected products are inactive or unavailable.");
        }

        var lines = request.Items
            .Select(item => new CalculationLine(
                products.Single(product => product.PublicId == item.ProductId),
                item.Quantity))
            .ToArray();
        var allocation = await branchAllocationService.AllocateAsync(
            address.Latitude,
            address.Longitude,
            lines.Select(line => (line.Product.Id, line.Quantity)).ToArray(),
            cancellationToken);
        var subtotal = decimal.Round(lines.Sum(line => line.Quantity * line.Product.Price), 2, MidpointRounding.AwayFromZero);
        return new Calculation(address, allocation, lines, subtotal, 0m);
    }

    /// <summary>
    /// Computes the Stage 2 charge block for a checkout. Exactly two active sets
    /// apply: GLOBAL charges (<see cref="Charge.ApplicableOnAll"/> — order-level
    /// base, subtotal − discount) and PRODUCT-MAPPED charges (active charges with
    /// a <see cref="ProductCharge"/> mapping to at least one ordered product —
    /// base = the sum of the ALREADY-ROUNDED line totals of the mapped lines,
    /// aggregated to exactly ONE <c>OrderCharge</c> row per Charge). Inactive
    /// charges never apply; their master mappings stay untouched. Both sets are
    /// read INSIDE the caller's authoritative transaction when
    /// <paramref name="track"/> is true, so a concurrent admin edit lands fully
    /// before or fully after the checkout. Grouping is by <see cref="Entity.Id"/>;
    /// the Stage 1 invariant (ApplicableOnAll = true ⇒ zero mappings) keeps the
    /// sets disjoint, and as pure defense a charge present in both resolves once
    /// as global — its current master flag — without mutating master data.
    /// Each amount is rounded independently (approved rule); order of application
    /// is irrelevant because every percentage applies to its own fixed base.
    /// </summary>
    private async Task<(ChargeCalculation Charges, IReadOnlyList<Charge> Masters)> ResolveChargesAsync(
        Calculation calculation,
        bool track,
        CancellationToken cancellationToken)
    {
        var orderBase = decimal.Round(
            calculation.Subtotal - calculation.DiscountAmount,
            2,
            MidpointRounding.AwayFromZero);
        var productIds = calculation.Lines.Select(line => line.Product.Id).Distinct().ToArray();

        // GLOBAL set: active charges whose master flag is ApplicableOnAll.
        var chargesQuery = track
            ? dbContext.Charges.Where(charge => charge.IsActive)
            : dbContext.Charges.AsNoTracking().Where(charge => charge.IsActive);
        var globalMasters = await chargesQuery
            .Where(charge => charge.ApplicableOnAll)
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToListAsync(cancellationToken);
        var globalIds = globalMasters.Select(charge => charge.Id).ToHashSet();

        // PRODUCT-MAPPED set: one bounded join (never N+1) over the ordered
        // products' mappings, restricted to active item-level charges.
        var mappedLinksQuery = (track
                ? dbContext.ProductCharges
                : dbContext.ProductCharges.AsNoTracking())
            .Where(link => productIds.Contains(link.ProductId))
            .Where(link => link.Charge.IsActive && !link.Charge.ApplicableOnAll)
            .Select(link => new { link.ProductId, Charge = link.Charge });
        var mappedLinks = await mappedLinksQuery.ToListAsync(cancellationToken);

        // The per-line contribution uses the SAME rounding as OrderItem.LineTotal
        // (and the checkout lines shown to the customer), so a mapped charge's
        // base is the sum of the established rounded line totals — never a raw
        // quantity × price recomputation.
        var lineTotalByProductId = calculation.Lines.ToDictionary(
            line => line.Product.Id,
            line => decimal.Round(line.Quantity * line.Product.Price, 2, MidpointRounding.AwayFromZero));

        var mappedMasters = mappedLinks
            .Select(link => link.Charge)
            .DistinctBy(charge => charge.Id)
            .Where(charge => !globalIds.Contains(charge.Id))
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToList();

        var entries = new List<(Charge Master, decimal BaseAmount)>();
        entries.AddRange(globalMasters.Select(charge => (Master: charge, orderBase)));
        foreach (var charge in mappedMasters)
        {
            var aggregatedBase = decimal.Round(
                mappedLinks
                    .Where(link => link.Charge.Id == charge.Id)
                    .Sum(link => lineTotalByProductId[link.ProductId]),
                2,
                MidpointRounding.AwayFromZero);
            entries.Add((charge, aggregatedBase));
        }

        var lines = entries
            .OrderBy(entry => entry.Master.ChargeType)
            .ThenBy(entry => entry.Master.ChargeCode)
            .Select(entry => new OrderChargeResult(
                entry.Master.ChargeType,
                entry.Master.ChargeCode,
                entry.Master.Description,
                entry.Master.Percentage,
                entry.BaseAmount,
                decimal.Round(entry.BaseAmount * entry.Master.Percentage / 100m, 2, MidpointRounding.AwayFromZero)))
            .ToArray();
        return (new ChargeCalculation(lines, lines.Sum(line => line.Amount)), [.. entries.Select(entry => entry.Master)]);
    }

    private IQueryable<Order> QueryOrders() => dbContext.Orders
        .Include(order => order.Items).ThenInclude(item => item.Product)
        .Include(order => order.Charges)
        .Include(order => order.Branch)
        .Include(order => order.CustomerAddress);

    private async Task LoadNavigationAsync(Order order, CancellationToken cancellationToken)
    {
        await dbContext.Entry(order).Reference(item => item.Branch).LoadAsync(cancellationToken);
        if (order.CustomerAddressId.HasValue)
        {
            await dbContext.Entry(order).Reference(item => item.CustomerAddress).LoadAsync(cancellationToken);
        }
        await dbContext.Entry(order).Collection(item => item.Items).LoadAsync(cancellationToken);
        foreach (var item in order.Items)
        {
            await dbContext.Entry(item).Reference(line => line.Product).LoadAsync(cancellationToken);
        }
    }

    private async Task<Order?> LoadOrderAsync(long customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        await QueryOrders().SingleOrDefaultAsync(order => order.CustomerId == customerId && order.IdempotencyKey == idempotencyKey, cancellationToken);

    private async Task<OrderResult> ToResultAsync(Order order, CancellationToken cancellationToken) =>
        (await ToResultsAsync([order], cancellationToken)).Single();

    private async Task<IReadOnlyList<OrderResult>> ToResultsAsync(
        IReadOnlyCollection<Order> orders,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var orderIds = orders.Select(order => order.Id).ToArray();
        var chargeRows = await dbContext.Set<OrderCharge>()
            .Where(charge => orderIds.Contains(charge.OrderId))
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToListAsync(cancellationToken);
        var chargesByOrder = chargeRows
            .GroupBy(charge => charge.OrderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<OrderCharge>)group.ToArray());
        var paymentAttempts = await dbContext.Payments
            .Where(payment => payment.OrderId.HasValue && orderIds.Contains(payment.OrderId.Value))
            .ToListAsync(cancellationToken);
        var payments = paymentAttempts
            .Where(payment => payment.OrderId.HasValue)
            .GroupBy(payment => payment.OrderId!.Value)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(payment => payment.Status is
                    PaymentStatus.Success or
                    PaymentStatus.PartiallyRefunded or
                    PaymentStatus.Refunded)
                .ThenByDescending(payment => payment.CreatedAt)
                .First());
        var deliveries = await dbContext.Deliveries
            .Where(delivery => delivery.OrderId.HasValue && orderIds.Contains(delivery.OrderId.Value))
            .ToDictionaryAsync(delivery => delivery.OrderId!.Value, cancellationToken);
        var cancellations = await dbContext.OrderCancellations
            .AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .ToDictionaryAsync(x => x.OrderId, cancellationToken);

        return orders
            .Select(order => order.ToResult(
                payments.GetValueOrDefault(order.Id),
                deliveries.GetValueOrDefault(order.Id),
                chargesByOrder.GetValueOrDefault(order.Id, []),
                cancellations.TryGetValue(order.Id, out var cancellation)
                    ? new OrderCancellationResult(
                        cancellation.ReasonCode, cancellation.Comment, cancellation.CancelledByUserId, cancellation.CancelledAt,
                        cancellation.RefundAmount.HasValue
                            ? new OrderCancellationRefundResult(
                                cancellation.GrossBasis ?? 0m,
                                cancellation.AppliedDeductionPercentage ?? 0m,
                                cancellation.DeductionAmount ?? 0m,
                                cancellation.RefundAmount.Value,
                                cancellation.RefundAmount.Value > 0 ? "Completed" : "None")
                            : null)
                    : null))
            .ToArray();
    }

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private sealed record Calculation(
        DeliveryAddress Address,
        BranchAllocationResult Allocation,
        IReadOnlyCollection<CalculationLine> Lines,
        decimal Subtotal,
        decimal DiscountAmount)
    {
        public CheckoutResult ToResult(ChargeCalculation charges) => new(
            Address.PublicId,
            Address.Label,
            Address.AddressLine1,
            Address.AddressLine2,
            Address.Locality,
            Address.City,
            Address.State,
            Address.PinCode,
            Address.ContactName,
            Address.ContactMobile,
            Allocation.BranchPublicId,
            Allocation.BranchCode,
            Allocation.BranchName,
            Allocation.DistanceKm,
            Lines.Select(line => new CheckoutLineResult(
                line.Product.PublicId,
                line.Product.Sku,
                line.Product.Name,
                line.Product.UnitOfMeasure,
                line.Quantity,
                line.Product.Price,
                decimal.Round(line.Quantity * line.Product.Price, 2, MidpointRounding.AwayFromZero))).ToArray(),
            Subtotal,
            DiscountAmount,
            charges.Lines,
            charges.Total,
            decimal.Round(Subtotal - DiscountAmount + charges.Total, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Server-authoritative active charges for one checkout: every Active master
    /// record, each rounded independently per the approved rule (base × % / 100,
    /// 2dp, AwayFromZero); the total is the exact sum of the rounded amounts.
    /// </summary>
    private sealed record ChargeCalculation(IReadOnlyCollection<OrderChargeResult> Lines, decimal Total)
    {
        public static readonly ChargeCalculation Empty = new([], 0m);
    }

    private sealed record CalculationLine(Product Product, decimal Quantity);

    private sealed record DeliveryAddress(
        long? Id,
        Guid? PublicId,
        string Label,
        string AddressLine1,
        string? AddressLine2,
        string Locality,
        string City,
        string State,
        string PinCode,
        string? Landmark,
        string? DeliveryInstructions,
        string ContactName,
        string ContactMobile,
        decimal Latitude,
        decimal Longitude)
    {
        public static DeliveryAddress FromManual(CheckoutAddressRequest request)
        {
            static string Required(string? value, string field, int maxLength)
            {
                var trimmed = value?.Trim() ?? string.Empty;
                if (trimmed.Length == 0 || trimmed.Length > maxLength)
                {
                    throw new ValidationAppException($"{field} is required and must be at most {maxLength} characters.", field);
                }
                return trimmed;
            }

            static string? Optional(string? value, string field, int maxLength)
            {
                var trimmed = value?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return null;
                if (trimmed.Length > maxLength)
                {
                    throw new ValidationAppException($"{field} must be at most {maxLength} characters.", field);
                }
                return trimmed;
            }

            if (request.Latitude is < -90 or > 90) throw new ValidationAppException("Latitude must be between -90 and 90.", "Latitude");
            if (request.Longitude is < -180 or > 180) throw new ValidationAppException("Longitude must be between -180 and 180.", "Longitude");

            var pin = Required(request.PinCode, "PinCode", 10);
            if (pin.Length != 6 || !pin.All(char.IsDigit)) throw new ValidationAppException("PIN code must contain exactly 6 digits.", "PinCode");

            return new DeliveryAddress(
                null,
                null,
                Optional(request.Label, "Label", 80) ?? "Manual",
                Required(request.AddressLine1, "AddressLine1", 200),
                Optional(request.AddressLine2, "AddressLine2", 200),
                Required(request.Locality, "Locality", 120),
                Required(request.City, "City", 100),
                Required(request.State, "State", 100),
                pin,
                Optional(request.Landmark, "Landmark", 160),
                Optional(request.DeliveryInstructions, "DeliveryInstructions", 500),
                Required(request.ContactName, "ContactName", 160),
                Required(request.ContactMobile, "ContactMobile", 20),
                request.Latitude,
                request.Longitude);
        }
    }
}

