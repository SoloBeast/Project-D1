using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Deliveries;

public sealed class DeliveryService(
    DoodhDirectDbContext dbContext,
    IClock clock,
    IPasswordHasher passwordHasher,
    IOtpDeliveryService otpDeliveryService,
    IDeliveryRealtimePublisher realtimePublisher,
    IOptions<DeliveryOptions> deliveryOptions,
    INotificationEventWriter notificationEventWriter) : IDeliveryService
{
    private readonly DeliveryOptions _options = deliveryOptions.Value;

    public async Task<DeliveryMaterializationResult> MaterializeEligibleAsync(
        DeliveryActor actor,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        EnsureActorHasBranches(actor);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        if (throughDate < today)
        {
            throw new ValidationAppException("The materialization date cannot be in the past.", "throughDate");
        }

        var orderQuery = dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Customer)
            .Where(x => x.Status == OrderStatus.Confirmed && !dbContext.Deliveries.Any(d => d.OrderId == x.Id));
        var occurrenceQuery = dbContext.SubscriptionDeliveries
            .AsNoTracking()
            .Include(x => x.Subscription).ThenInclude(x => x.Customer)
            .Include(x => x.Subscription).ThenInclude(x => x.CustomerAddress)
            .Where(x => x.Status == SubscriptionDeliveryStatus.Scheduled &&
                        x.Subscription.Status == SubscriptionStatus.Active &&
                        x.ScheduledDate <= throughDate &&
                        !dbContext.Deliveries.Any(d => d.SubscriptionDeliveryId == x.Id));

        if (!actor.HasGlobalAccess)
        {
            orderQuery = orderQuery.Where(x => actor.BranchIds.Contains(x.BranchId));
            occurrenceQuery = occurrenceQuery.Where(x => actor.BranchIds.Contains(x.BranchId));
        }

        var orders = await orderQuery.ToListAsync(cancellationToken);
        var occurrences = await occurrenceQuery.ToListAsync(cancellationToken);
        var now = clock.UtcNow;

        foreach (var order in orders)
        {
            dbContext.Deliveries.Add(Delivery.ForOrder(
                order.Id,
                order.CustomerId,
                order.BranchId,
                today,
                order.OrderNumber,
                order.ContactNameSnapshot,
                order.ContactMobileSnapshot,
                FormatOrderAddress(order),
                order.DeliveryInstructionsSnapshot,
                order.LatitudeSnapshot,
                order.LongitudeSnapshot));
        }

        foreach (var occurrence in occurrences)
        {
            var subscription = occurrence.Subscription;
            var address = subscription.CustomerAddress;
            dbContext.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                occurrence.Id,
                subscription.CustomerId,
                occurrence.BranchId,
                occurrence.ScheduledDate,
                $"SUB-{subscription.PublicId:N}-{occurrence.ScheduledDate:yyyyMMdd}",
                address.ContactName,
                address.ContactMobile,
                occurrence.AddressSnapshot,
                address.DeliveryInstructions,
                address.Latitude,
                address.Longitude));
        }

        if (orders.Count + occurrences.Count > 0)
        {
            AddAudit(actor.UserId, "DELIVERY.MATERIALIZE", "Delivery", throughDate.ToString("yyyy-MM-dd"), null,
                new { OrdersCreated = orders.Count, SubscriptionOccurrencesCreated = occurrences.Count }, null, now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new DeliveryMaterializationResult(orders.Count, occurrences.Count);
    }

    public async Task<IReadOnlyList<DeliveryResult>> GetTodayForStaffAsync(
        DeliveryActor actor,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var deliveries = await OperationsQuery()
            .Where(x => x.AssignedEmployeeId == actor.UserId && x.ScheduledDate == date)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.AssignedAtUtc)
            .ToListAsync(cancellationToken);
        return deliveries.Select(ToResult).ToArray();
    }

    public async Task<IReadOnlyList<DeliveryResult>> GetForBranchAsync(
        DeliveryActor actor,
        long branchId,
        DateOnly? date,
        DeliveryStatus? status,
        CancellationToken cancellationToken)
    {
        EnsureBranchAccess(actor, branchId);
        var query = OperationsQuery().Where(x => x.BranchId == branchId);
        if (date.HasValue) query = query.Where(x => x.ScheduledDate == date.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var deliveries = await query.OrderByDescending(x => x.ScheduledDate).ThenBy(x => x.Status).ToListAsync(cancellationToken);
        return deliveries.Select(ToResult).ToArray();
    }

    public async Task<IReadOnlyList<DeliveryEmployeeResult>> GetEmployeesAsync(
        DeliveryActor actor,
        long branchId,
        CancellationToken cancellationToken)
    {
        EnsureBranchAccess(actor, branchId);
        return await dbContext.UserRoles
            .AsNoTracking()
            .Where(x => x.BranchId == branchId && x.Role.Code == AuthorizationCodes.DeliveryStaff && x.User.IsActive)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new DeliveryEmployeeResult(x.User.PublicId, x.User.DisplayName ?? x.User.Email ?? x.User.Mobile ?? "Delivery employee", branchId))
            .ToListAsync(cancellationToken);
    }

    public async Task<DeliveryResult> GetForOperationsAsync(
        DeliveryActor actor,
        Guid deliveryId,
        bool requireAssignment,
        CancellationToken cancellationToken)
    {
        var delivery = await FindAsync(deliveryId, cancellationToken);
        EnsureOperationalAccess(actor, delivery, requireAssignment);
        return ToResult(delivery);
    }

    public async Task<IReadOnlyList<CustomerDeliveryResult>> GetForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken)
    {
        var deliveries = await OperationsQuery()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.ScheduledDate)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return deliveries.Select(ToCustomerResult).ToArray();
    }

    public async Task<CustomerDeliveryResult> GetForCustomerAsync(
        long customerId,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await OperationsQuery().SingleOrDefaultAsync(
            x => x.PublicId == deliveryId && x.CustomerId == customerId,
            cancellationToken) ?? throw new NotFoundException("The delivery was not found.");
        return ToCustomerResult(delivery);
    }

    public async Task<DeliveryResult> AssignAsync(
        DeliveryActor actor,
        Guid deliveryId,
        AssignDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var delivery = await FindAsync(deliveryId, cancellationToken);
        EnsureBranchAccess(actor, delivery.BranchId);
        var employee = await FindEligibleEmployeeAsync(request.EmployeeId, delivery.BranchId, cancellationToken);
        var previousEmployeeId = delivery.AssignedEmployeeId;
        var now = clock.UtcNow;
        Mutate(() => delivery.Assign(employee.Id, actor.UserId, now, request.Reason));
        if (delivery.Order is not null) Mutate(delivery.Order.AssignForDelivery);
        AddAudit(actor.UserId, previousEmployeeId.HasValue ? "DELIVERY.REASSIGN" : "DELIVERY.ASSIGN", "Delivery",
            delivery.PublicId.ToString(), new { EmployeeId = previousEmployeeId }, new { EmployeeId = employee.PublicId }, request.Reason, now);
        AddDeliveryEvent(
            delivery,
            NotificationEventTypes.DeliveryAssigned,
            $"delivery:{delivery.PublicId:N}:assigned:{now.Ticks}",
            $"Your delivery has been assigned to {employee.DisplayName ?? "a delivery employee"}.",
            now,
            new Dictionary<string, string>
            {
                ["employeeId"] = employee.PublicId.ToString(),
                ["employeeName"] = employee.DisplayName ?? "Delivery employee"
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PublishChangedAsync(delivery.PublicId, cancellationToken);
    }

    public Task<DeliveryResult> PickUpAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryNotesRequest request,
        CancellationToken cancellationToken) =>
        TransitionAsync(actor, deliveryId, "DELIVERY.PICK_UP", d => d.PickUp(actor.UserId, clock.UtcNow, request.Remarks), cancellationToken);

    public async Task<DeliveryResult> StartAsync(DeliveryActor actor, Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        var now = clock.UtcNow;
        Mutate(() => delivery.Start(actor.UserId, now));
        if (delivery.Order is not null) Mutate(delivery.Order.StartDelivery);
        AddTransitionAudit(actor.UserId, "DELIVERY.START", delivery);
        AddDeliveryEvent(
            delivery,
            NotificationEventTypes.DeliveryStarted,
            $"delivery:{delivery.PublicId:N}:started:{now.Ticks}",
            "Your delivery is on the way.",
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PublishChangedAsync(delivery.PublicId, cancellationToken);
    }

    public async Task<DeliveryResult> ArriveAsync(
        DeliveryActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        var now = clock.UtcNow;
        Mutate(() => delivery.Arrive(actor.UserId, now));
        AddTransitionAudit(actor.UserId, "DELIVERY.ARRIVE", delivery);
        AddDeliveryEvent(
            delivery,
            NotificationEventTypes.DeliveryNearCustomer,
            $"delivery:{delivery.PublicId:N}:near-customer:{now.Ticks}",
            "Your delivery has arrived nearby.",
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PublishChangedAsync(delivery.PublicId, cancellationToken);
    }

    public async Task IssueOtpAsync(DeliveryActor actor, Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        if (delivery.Status != DeliveryStatus.Arrived)
        {
            throw new BusinessRuleException("Delivery OTP can only be issued after arrival.");
        }
        if (delivery.OtpVerifiedAtUtc.HasValue)
        {
            throw new ConflictException("The delivery OTP has already been verified.");
        }

        var now = clock.UtcNow;
        var activeOtps = delivery.Otps.Where(x => !x.ConsumedAtUtc.HasValue).ToArray();
        foreach (var activeOtp in activeOtps)
        {
            if (activeOtp.ExpiresAtUtc > now && activeOtp.AttemptCount < activeOtp.MaximumAttempts)
            {
                throw new ConflictException("An active delivery OTP already exists.");
            }
        }

        var code = CreateNumericCode(_options.OtpCodeLength);
        var otp = new DeliveryOtp(delivery.Id, passwordHasher.Hash(code), now.AddMinutes(_options.OtpExpiryMinutes), _options.OtpMaximumAttempts, now);
        dbContext.DeliveryOtps.Add(otp);
        AddAudit(actor.UserId, "DELIVERY.OTP_ISSUE", "Delivery", delivery.PublicId.ToString(), null,
            new { otp.ExpiresAtUtc, otp.MaximumAttempts }, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await otpDeliveryService.SendAsync(delivery.CustomerMobileSnapshot, code, cancellationToken);
    }

    public async Task<DeliveryResult> VerifyOtpAsync(
        DeliveryActor actor,
        Guid deliveryId,
        VerifyDeliveryOtpRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new ValidationAppException("The delivery OTP is required.", "code");
        }

        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        if (delivery.Status != DeliveryStatus.Arrived)
        {
            throw new BusinessRuleException("Delivery OTP can only be verified after arrival.");
        }
        var now = clock.UtcNow;
        var otp = delivery.Otps
            .Where(x => !x.ConsumedAtUtc.HasValue)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault() ?? throw new NotFoundException("No delivery OTP is available for verification.");

        try
        {
            otp.EnsureVerifiable(now);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessRuleException(exception.Message);
        }

        if (!passwordHasher.Verify(otp.CodeHash, request.Code.Trim()))
        {
            Mutate(() => otp.RecordFailedAttempt(now));
            AddAudit(actor.UserId, "DELIVERY.OTP_FAILURE", "Delivery", delivery.PublicId.ToString(),
                null, new { otp.AttemptCount, otp.MaximumAttempts }, null, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new ValidationAppException("The delivery OTP is invalid.", "code");
        }

        Mutate(() => otp.Consume(now));
        Mutate(() => delivery.RecordOtpVerified(actor.UserId, now));
        AddAudit(actor.UserId, "DELIVERY.OTP_SUCCESS", "Delivery", delivery.PublicId.ToString(), null, null, null, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PublishChangedAsync(delivery.PublicId, cancellationToken);
    }

    public async Task<DeliveryResult> CompleteAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryNotesRequest request,
        CancellationToken cancellationToken)
    {
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
            var now = clock.UtcNow;
            Mutate(() => delivery.Complete(actor.UserId, now, request.Remarks));
            if (delivery.Order is not null)
            {
                Mutate(delivery.Order.MarkDelivered);
            }
            else
            {
                var occurrence = delivery.SubscriptionDelivery!;
                Mutate(() => occurrence.Subscription.MarkDelivered(occurrence, now));
            }
            AddTransitionAudit(actor.UserId, "DELIVERY.COMPLETE", delivery);
            AddDeliveryEvent(
                delivery,
                NotificationEventTypes.DeliveryCompleted,
                $"delivery:{delivery.PublicId:N}:completed:{now.Ticks}",
                "Your delivery has been completed.",
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return await PublishChangedAsync(deliveryId, cancellationToken);
    }

    public async Task<DeliveryResult> FailAsync(
        DeliveryActor actor,
        Guid deliveryId,
        FailDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
            var now = clock.UtcNow;
            Mutate(() => delivery.Fail(actor.UserId, now, request.Reason, request.Remarks, request.Latitude, request.Longitude));
            if (delivery.Order is not null)
            {
                Mutate(delivery.Order.MarkDeliveryFailed);
            }
            else
            {
                var occurrence = delivery.SubscriptionDelivery!;
                Mutate(() => occurrence.Subscription.MarkFailed(occurrence, now));
            }
            AddAudit(actor.UserId, "DELIVERY.FAIL", "Delivery", delivery.PublicId.ToString(), null,
                new { delivery.Status, delivery.FailureReason, delivery.FailureLatitude, delivery.FailureLongitude }, request.Remarks, now);
            AddDeliveryEvent(
                delivery,
                NotificationEventTypes.DeliveryFailed,
                $"delivery:{delivery.PublicId:N}:failed:{now.Ticks}",
                "Your delivery could not be completed.",
                now,
                new Dictionary<string, string>
                {
                    ["reason"] = request.Reason
                });
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return await PublishChangedAsync(deliveryId, cancellationToken);
    }

    public async Task<DeliveryLocationResult> RecordLocationAsync(
        DeliveryActor actor,
        Guid deliveryId,
        DeliveryLocationRequest request,
        CancellationToken cancellationToken)
    {
        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        Mutate(() => delivery.EnsureCanRecordLocation(actor.UserId));
        var now = clock.UtcNow;
        var minimum = now.AddMinutes(-_options.MaximumLocationAgeMinutes);
        var maximum = now.AddMinutes(_options.MaximumLocationFutureSkewMinutes);
        if (request.RecordedAtUtc.Kind != DateTimeKind.Utc || request.RecordedAtUtc < minimum || request.RecordedAtUtc > maximum)
        {
            throw new ValidationAppException("The location timestamp is outside the permitted UTC window.", "recordedAtUtc");
        }

        DeliveryLocation location;
        try
        {
            location = new DeliveryLocation(delivery.Id, actor.UserId, request.Latitude, request.Longitude, request.AccuracyMetres, request.RecordedAtUtc);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message);
        }
        dbContext.DeliveryLocations.Add(location);
        await PruneLocationsAsync(delivery.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var result = ToLocationResult(location);
        await realtimePublisher.LocationChangedAsync(delivery.PublicId, result, cancellationToken);
        return result;
    }

    private async Task<DeliveryResult> TransitionAsync(
        DeliveryActor actor,
        Guid deliveryId,
        string action,
        Action<Delivery> transition,
        CancellationToken cancellationToken)
    {
        var delivery = await FindAssignedAsync(actor, deliveryId, cancellationToken);
        Mutate(() => transition(delivery));
        AddTransitionAudit(actor.UserId, action, delivery);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PublishChangedAsync(delivery.PublicId, cancellationToken);
    }

    private async Task<Delivery> FindAssignedAsync(DeliveryActor actor, Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await FindAsync(deliveryId, cancellationToken);
        EnsureOperationalAccess(actor, delivery, true);
        return delivery;
    }

    private async Task<Delivery> FindAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        await OperationsQuery().SingleOrDefaultAsync(x => x.PublicId == deliveryId, cancellationToken)
        ?? throw new NotFoundException("The delivery was not found.");

    private IQueryable<Delivery> OperationsQuery() => dbContext.Deliveries
        .Include(x => x.Order)
        .Include(x => x.SubscriptionDelivery).ThenInclude(x => x!.Subscription)
        .Include(x => x.Customer)
        .Include(x => x.AssignedEmployee)
        .Include(x => x.Assignments).ThenInclude(x => x.Employee)
        .Include(x => x.Assignments).ThenInclude(x => x.AssignedByUser)
        .Include(x => x.Otps)
        .Include(x => x.Locations);

    private async Task<User> FindEligibleEmployeeAsync(Guid employeeId, long branchId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleOrDefaultAsync(x => x.PublicId == employeeId && x.IsActive &&
                x.UserRoles.Any(r => r.BranchId == branchId && r.Role.Code == AuthorizationCodes.DeliveryStaff), cancellationToken)
        ?? throw new ValidationAppException("The selected employee is not active delivery staff for this branch.", "employeeId");

    private void EnsureOperationalAccess(DeliveryActor actor, Delivery delivery, bool requireAssignment)
    {
        if (requireAssignment)
        {
            if (delivery.AssignedEmployeeId != actor.UserId)
            {
                throw new NotFoundException("The delivery was not found.");
            }
            return;
        }
        if (delivery.AssignedEmployeeId == actor.UserId) return;
        EnsureBranchAccess(actor, delivery.BranchId);
    }

    private static void EnsureActorHasBranches(DeliveryActor actor)
    {
        if (!actor.HasGlobalAccess && actor.BranchIds.Count == 0)
        {
            throw new BusinessRuleException("A branch assignment is required for delivery operations.");
        }
    }

    private static void EnsureBranchAccess(DeliveryActor actor, long branchId)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(branchId))
        {
            throw new NotFoundException("The delivery resource was not found for the permitted branch scope.");
        }
    }

    private async Task<DeliveryResult> PublishChangedAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await OperationsQuery().AsNoTracking().SingleAsync(x => x.PublicId == deliveryId, cancellationToken);
        var result = ToResult(delivery);
        await realtimePublisher.DeliveryChangedAsync(result, cancellationToken);
        return result;
    }

    private async Task PruneLocationsAsync(long deliveryId, DateTime now, CancellationToken cancellationToken)
    {
        var expired = await dbContext.DeliveryLocations
            .Where(x => x.RecordedAtUtc < now.AddDays(-_options.LocationRetentionDays))
            .ToListAsync(cancellationToken);
        if (expired.Count > 0) dbContext.DeliveryLocations.RemoveRange(expired);

        var excess = await dbContext.DeliveryLocations
            .Where(x => x.DeliveryId == deliveryId)
            .OrderByDescending(x => x.RecordedAtUtc)
            .Skip(_options.MaximumLocationsPerDelivery - 1)
            .ToListAsync(cancellationToken);
        if (excess.Count > 0) dbContext.DeliveryLocations.RemoveRange(excess);
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

    private void AddTransitionAudit(long userId, string action, Delivery delivery) =>
        AddAudit(userId, action, "Delivery", delivery.PublicId.ToString(), null, new { delivery.Status }, null, clock.UtcNow);

    private void AddDeliveryEvent(
        Delivery delivery,
        string eventType,
        string eventKey,
        string message,
        DateTime occurredAtUtc,
        IReadOnlyDictionary<string, string>? additionalVariables = null)
    {
        var variables = new Dictionary<string, string>
        {
            ["deliveryId"] = delivery.PublicId.ToString(),
            ["message"] = message,
            ["referenceNumber"] = delivery.ReferenceNumber
        };
        if (additionalVariables is not null)
        {
            foreach (var variable in additionalVariables)
            {
                variables[variable.Key] = variable.Value;
            }
        }

        notificationEventWriter.Add(new NotificationEventRequest(
            delivery.CustomerId,
            eventType,
            eventKey,
            variables,
            $"/deliveries/{delivery.PublicId}",
            occurredAtUtc));
    }

    private void AddAudit(
        long? userId,
        string action,
        string entityType,
        string entityId,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime createdAtUtc) =>
        dbContext.AuditLogs.Add(new AuditLog(
            userId,
            action,
            entityType,
            entityId,
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            reason,
            createdAtUtc));

    private static void Mutate(Action operation)
    {
        try
        {
            operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessRuleException(exception.Message);
        }
    }

    private static DeliveryResult ToResult(Delivery delivery)
    {
        var latest = delivery.Locations.OrderByDescending(x => x.RecordedAtUtc).FirstOrDefault();
        return new DeliveryResult(
            delivery.PublicId,
            delivery.SourceType,
            delivery.ReferenceNumber,
            delivery.Status,
            delivery.ScheduledDate,
            delivery.BranchId,
            delivery.Customer.PublicId,
            delivery.CustomerNameSnapshot,
            delivery.CustomerMobileSnapshot,
            delivery.DestinationAddressSnapshot,
            delivery.DeliveryInstructionsSnapshot,
            delivery.DestinationLatitude,
            delivery.DestinationLongitude,
            delivery.AssignedEmployee?.PublicId,
            delivery.AssignedEmployee?.DisplayName,
            delivery.AssignedAtUtc,
            delivery.PickedUpAtUtc,
            delivery.OutForDeliveryAtUtc,
            delivery.ArrivedAtUtc,
            delivery.OtpVerifiedAtUtc,
            delivery.CompletedAtUtc,
            delivery.FailedAtUtc,
            delivery.FailureReason,
            delivery.Remarks,
            delivery.OperationalNotes,
            delivery.IsTrackingActive,
            latest is null ? null : ToLocationResult(latest),
            delivery.Assignments.OrderBy(x => x.AssignedAtUtc).Select(x => new DeliveryAssignmentResult(
                x.Employee.PublicId,
                x.Employee.DisplayName,
                x.AssignedByUser.PublicId,
                x.AssignedAtUtc,
                x.Reason)).ToArray());
    }

    private static CustomerDeliveryResult ToCustomerResult(Delivery delivery)
    {
        var latest = delivery.IsTrackingActive
            ? delivery.Locations.OrderByDescending(x => x.RecordedAtUtc).FirstOrDefault()
            : null;
        return new CustomerDeliveryResult(
            delivery.PublicId,
            delivery.SourceType,
            delivery.ReferenceNumber,
            delivery.Status,
            delivery.ScheduledDate,
            delivery.DestinationAddressSnapshot,
            delivery.AssignedEmployee?.PublicId,
            delivery.AssignedEmployee?.DisplayName,
            delivery.IsTrackingActive,
            latest is null ? null : ToLocationResult(latest),
            delivery.CompletedAtUtc,
            delivery.FailedAtUtc,
            delivery.FailureReason);
    }

    private static DeliveryLocationResult ToLocationResult(DeliveryLocation location) =>
        new(location.Latitude, location.Longitude, location.AccuracyMetres, location.RecordedAtUtc);

    private static string CreateNumericCode(int length)
    {
        var maximum = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(maximum).ToString($"D{length}");
    }

    private static string FormatOrderAddress(Order order)
    {
        var lines = new[]
        {
            order.AddressLine1Snapshot,
            order.AddressLine2Snapshot,
            order.LocalitySnapshot,
            order.CitySnapshot,
            order.StateSnapshot,
            order.PinCodeSnapshot,
            order.LandmarkSnapshot
        };
        return string.Join(", ", lines.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
