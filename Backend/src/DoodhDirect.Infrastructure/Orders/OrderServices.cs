using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Infrastructure.Persistence;
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

        var eligible = candidates
            .Select(branch => new
            {
                Branch = branch,
                DistanceKm = DistanceKm(latitude, longitude, branch.Latitude, branch.Longitude)
            })
            .Where(candidate => !candidate.Branch.ServiceRadiusKm.HasValue ||
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
    IIndiaTimeProvider timeProvider) : IOrderService
{
    public async Task<CheckoutResult> PreviewAsync(long customerId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        var calculation = await CalculateAsync(customerId, request, cancellationToken);
        return calculation.ToResult();
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
        var order = new Order(
            customerId,
            calculation.Address.Id,
            calculation.Allocation.BranchId,
            idempotencyKey.Trim(),
            CreateOrderNumber(),
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

        dbContext.Orders.Add(order);
        var notificationEventKey = $"order:{order.PublicId:N}:created";
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.OrderCreated,
            notificationEventKey,
            new Dictionary<string, string>
            {
                ["message"] = $"Order {order.OrderNumber} has been created."
            },
            $"/orders/{order.PublicId}"));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(order).State = EntityState.Detached;
            foreach (var item in order.Items)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            var notificationEvent = dbContext.NotificationEvents.Local
                .SingleOrDefault(item => item.EventKey == notificationEventKey);
            if (notificationEvent is not null)
            {
                dbContext.Entry(notificationEvent).State = EntityState.Detached;
            }

            var duplicate = await LoadOrderAsync(customerId, idempotencyKey.Trim(), cancellationToken);
            if (duplicate is not null) return await ToResultAsync(duplicate, cancellationToken);
            throw;
        }

        await LoadNavigationAsync(order, cancellationToken);
        return await ToResultAsync(order, cancellationToken);
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

    public async Task<OrderResult> GetAsync(long customerId, Guid orderId, bool bypassOwnership, CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(order => order.PublicId == orderId && (bypassOwnership || order.CustomerId == customerId), cancellationToken);
        return order is null
            ? throw new NotFoundException("Order was not found.")
            : await ToResultAsync(order, cancellationToken);
    }

    public async Task<OrderResult> CancelAsync(long customerId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await QueryOrders()
            .SingleOrDefaultAsync(order => order.PublicId == orderId && order.CustomerId == customerId, cancellationToken)
            ?? throw new NotFoundException("Order was not found.");
        var now = timeProvider.Now;
        try
        {
            order.Cancel(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessRuleException(exception.Message);
        }

        var deliveries = await dbContext.Deliveries
            .Include(delivery => delivery.Otps)
            .Where(delivery => delivery.OrderId == order.Id)
            .ToListAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            foreach (var otp in delivery.Otps)
            {
                otp.Invalidate(now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await ToResultAsync(order, cancellationToken);
    }

    private async Task<Calculation> CalculateAsync(long customerId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (customerId <= 0) throw new UnauthorizedAppException();
        if (request.AddressId == Guid.Empty) throw new ValidationAppException("A delivery address is required.", "AddressId");
        try
        {
            OrderValidation.ValidateItems(request.Items);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }

        var address = await dbContext.CustomerAddresses
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PublicId == request.AddressId && item.UserId == customerId && item.IsActive, cancellationToken)
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

    private IQueryable<Order> QueryOrders() => dbContext.Orders
        .Include(order => order.Items).ThenInclude(item => item.Product)
        .Include(order => order.Branch)
        .Include(order => order.CustomerAddress);

    private async Task LoadNavigationAsync(Order order, CancellationToken cancellationToken)
    {
        await dbContext.Entry(order).Reference(item => item.Branch).LoadAsync(cancellationToken);
        await dbContext.Entry(order).Reference(item => item.CustomerAddress).LoadAsync(cancellationToken);
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

        return orders
            .Select(order => order.ToResult(
                payments.GetValueOrDefault(order.Id),
                deliveries.GetValueOrDefault(order.Id)))
            .ToArray();
    }

    private string CreateOrderNumber() => $"DD-{timeProvider.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25].ToUpperInvariant();

    private sealed record Calculation(
        CustomerAddress Address,
        BranchAllocationResult Allocation,
        IReadOnlyCollection<CalculationLine> Lines,
        decimal Subtotal,
        decimal DiscountAmount)
    {
        public CheckoutResult ToResult() => new(
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
            Subtotal - DiscountAmount);
    }

    private sealed record CalculationLine(Product Product, decimal Quantity);
}

