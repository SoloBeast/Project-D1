using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.MilkTesting;

public sealed class MilkTestService(
    DoodhDirectDbContext dbContext,
    IClock clock,
    IMediaStorage mediaStorage,
    IMilkTestImageValidator imageValidator,
    INotificationEventWriter notificationEventWriter) : IMilkTestService
{
    private const decimal MaximumReadingMagnitude = 999999999999.999999m;

    public async Task<CustomerMilkTestResult> RequestAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        CustomerMilkTestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await dbContext.Deliveries.SingleOrDefaultAsync(
                x => x.PublicId == deliveryId && x.CustomerId == actor.UserId,
                cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

            if (delivery.Status is DeliveryStatus.Delivered or DeliveryStatus.Failed)
            {
                throw new BusinessRuleException("A doorstep test can only be requested for an active delivery.");
            }
            if (await dbContext.MilkTests.AnyAsync(x => x.DeliveryId == delivery.Id, cancellationToken))
            {
                throw new ConflictException("A doorstep test has already been requested for this delivery.");
            }

            var now = clock.UtcNow;
            var milkTest = new MilkTest(delivery.Id, delivery.CustomerId, delivery.BranchId, actor.UserId, now);
            dbContext.MilkTests.Add(milkTest);
            AddAudit(actor.UserId, "MILK_TEST.REQUEST", milkTest.PublicId, null,
                new { DeliveryId = delivery.PublicId, milkTest.Status }, null, now);
            AddAudit(actor.UserId, "MILK_TEST.CREATE", milkTest.PublicId, null,
                new { DeliveryId = delivery.PublicId, milkTest.CustomerId, milkTest.BranchId }, null, now);
            AddMilkTestEvent(
                milkTest,
                delivery.PublicId,
                NotificationEventTypes.MilkTestRequested,
                $"milk-test:{milkTest.PublicId:N}:requested",
                "Your doorstep milk test has been requested.",
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToCustomerResult(milkTest, delivery.PublicId);
        }, cancellationToken);
        return result!;
    }

    public async Task<CustomerMilkTestResult?> GetForCustomerAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var milkTest = await TestQuery(asNoTracking: true)
            .SingleOrDefaultAsync(
                x => x.Delivery.PublicId == deliveryId && x.CustomerId == actor.UserId,
                cancellationToken);
        return milkTest is null ? null : ToCustomerResult(milkTest, milkTest.Delivery.PublicId);
    }

    public async Task<StaffMilkTestResult?> GetForStaffAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var milkTest = await TestQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.Delivery.PublicId == deliveryId, cancellationToken);
        if (milkTest is null)
        {
            return null;
        }
        EnsureStaffAccess(actor, milkTest);
        return ToStaffResult(milkTest, milkTest.Delivery.PublicId);
    }

    public async Task<MilkTestImageResult> UploadImageAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken)
    {
        await using var validated = await imageValidator.ValidateAsync(
            content,
            fileName,
            declaredContentType,
            cancellationToken);

        var milkTest = await TestQuery().SingleOrDefaultAsync(
            x => x.PublicId == milkTestId,
            cancellationToken) ?? throw new NotFoundException("The doorstep test was not found.");
        EnsureStaffAccess(actor, milkTest);
        if (milkTest.Status != MilkTestStatus.Requested)
        {
            throw new ConflictException("Images cannot be added after the doorstep test is completed.");
        }
        if (milkTest.Delivery.Status is DeliveryStatus.Delivered or DeliveryStatus.Failed)
        {
            throw new BusinessRuleException("Images cannot be added to a terminal delivery.");
        }

        var now = clock.UtcNow;
        var extension = validated.ContentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new ValidationAppException("The validated image type is unsupported.", "image")
        };
        var storageKey = $"{now:yyyy/MM}/{milkTest.BranchId}/{milkTest.PublicId:N}/{Guid.NewGuid():N}{extension}";
        var stored = await mediaStorage.SaveAsync(
            storageKey,
            validated.Content,
            validated.ContentType,
            cancellationToken);

        try
        {
            if (stored.FileSize != validated.FileSize)
            {
                throw new InvalidOperationException("The stored media size does not match the validated image size.");
            }

            var image = new MilkTestImage(
                milkTest.Id,
                stored.StorageKey,
                validated.FileName,
                validated.ContentType,
                stored.FileSize,
                actor.UserId,
                now);
            Mutate(() => milkTest.AddImage(image));
            AddAudit(actor.UserId, "MILK_TEST.IMAGE_UPLOAD", milkTest.PublicId, null,
                new { ImageId = image.PublicId, image.FileName, image.ContentType, image.FileSize }, null, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToImageResult(milkTest.PublicId, image);
        }
        catch
        {
            await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }
    }

    public async Task<StoredMediaContent> OpenImageForCustomerAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var image = await dbContext.MilkTestImages
            .AsNoTracking()
            .Include(x => x.MilkTest)
            .SingleOrDefaultAsync(
                x => x.PublicId == imageId &&
                     x.MilkTest.PublicId == milkTestId &&
                     x.MilkTest.CustomerId == actor.UserId &&
                     x.MilkTest.Status == MilkTestStatus.Completed,
                cancellationToken) ?? throw new NotFoundException("The doorstep test image was not found.");

        var stored = await mediaStorage.OpenReadAsync(image.StorageKey, cancellationToken);
        return new StoredMediaContent(stored.Content, image.ContentType, image.FileSize);
    }

    public async Task<StaffMilkTestResult> CompleteAsync(
        MilkTestActor actor,
        Guid milkTestId,
        CompleteMilkTestRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCompletionRequest(request);
        StaffMilkTestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var milkTest = await TestQuery().SingleOrDefaultAsync(
                x => x.PublicId == milkTestId,
                cancellationToken) ?? throw new NotFoundException("The doorstep test was not found.");
            EnsureStaffAccess(actor, milkTest);
            if (milkTest.Delivery.Status != DeliveryStatus.Arrived)
            {
                throw new BusinessRuleException("The doorstep test can only be completed after delivery arrival.");
            }

            foreach (var parameter in request.Parameters)
            {
                Mutate(() => milkTest.AddParameter(parameter.Code, parameter.Name, parameter.Value, parameter.Unit));
            }
            var now = clock.UtcNow;
            Mutate(() => milkTest.Complete(actor.UserId, now, request.Remarks));
            AddAudit(actor.UserId, "MILK_TEST.COMPLETE", milkTest.PublicId,
                new { Status = MilkTestStatus.Requested },
                new { milkTest.Status, milkTest.CompletedAtUtc, ReadingCount = milkTest.Parameters.Count, ImageCount = milkTest.Images.Count },
                request.Remarks,
                now);
            AddMilkTestEvent(
                milkTest,
                milkTest.Delivery.PublicId,
                NotificationEventTypes.MilkTestCompleted,
                $"milk-test:{milkTest.PublicId:N}:completed",
                "Your doorstep milk test results are ready.",
                now,
                new Dictionary<string, string>
                {
                    ["readingCount"] = milkTest.Parameters.Count.ToString()
                });
            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToStaffResult(milkTest, milkTest.Delivery.PublicId);
        }, cancellationToken);
        return result!;
    }

    public Task<CustomerMilkTestResult> ConfirmAsync(
        MilkTestActor actor,
        Guid milkTestId,
        DecideMilkTestRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, milkTestId, request, confirm: true, cancellationToken);

    public Task<CustomerMilkTestResult> RejectAsync(
        MilkTestActor actor,
        Guid milkTestId,
        DecideMilkTestRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, milkTestId, request, confirm: false, cancellationToken);

    private async Task<CustomerMilkTestResult> DecideAsync(
        MilkTestActor actor,
        Guid milkTestId,
        DecideMilkTestRequest request,
        bool confirm,
        CancellationToken cancellationToken)
    {
        ValidateLength(request.Remarks, "remarks", 1000);
        CustomerMilkTestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var milkTest = await TestQuery().SingleOrDefaultAsync(
                x => x.PublicId == milkTestId && x.CustomerId == actor.UserId,
                cancellationToken) ?? throw new NotFoundException("The doorstep test was not found.");
            var previousDecision = milkTest.CustomerDecision;
            var now = clock.UtcNow;
            Mutate(() =>
            {
                if (confirm)
                {
                    milkTest.Confirm(now, request.Remarks);
                }
                else
                {
                    milkTest.Reject(now, request.Remarks);
                }
            });
            AddAudit(actor.UserId, confirm ? "MILK_TEST.CONFIRM" : "MILK_TEST.REJECT", milkTest.PublicId,
                new { CustomerDecision = previousDecision },
                new { milkTest.CustomerDecision, milkTest.ConfirmedAtUtc, milkTest.RejectedAtUtc },
                request.Remarks,
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToCustomerResult(milkTest, milkTest.Delivery.PublicId);
        }, cancellationToken);
        return result!;
    }

    private IQueryable<MilkTest> TestQuery(bool asNoTracking = false)
    {
        IQueryable<MilkTest> query = dbContext.MilkTests
            .Include(x => x.Delivery)
            .Include(x => x.Parameters)
            .Include(x => x.Images);
        return asNoTracking ? query.AsNoTracking() : query;
    }

    private static void EnsureStaffAccess(MilkTestActor actor, MilkTest milkTest)
    {
        if (milkTest.Delivery.AssignedEmployeeId != actor.UserId ||
            (!actor.HasGlobalAccess && !actor.BranchIds.Contains(milkTest.BranchId)))
        {
            throw new NotFoundException("The doorstep test was not found.");
        }
    }

    private static void ValidateCompletionRequest(CompleteMilkTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parameters is null || request.Parameters.Count == 0)
        {
            throw new ValidationAppException("At least one reading is required.", "parameters");
        }
        ValidateLength(request.Remarks, "remarks", 1000);

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in request.Parameters)
        {
            if (parameter is null)
            {
                throw new ValidationAppException("A reading cannot be null.", "parameters");
            }
            ValidateRequired(parameter.Code, "code", 80);
            ValidateRequired(parameter.Name, "name", 160);
            ValidateRequired(parameter.Unit, "unit", 40);
            if (!codes.Add(parameter.Code.Trim()))
            {
                throw new ValidationAppException($"Reading code '{parameter.Code.Trim()}' is duplicated.", "parameters");
            }
            if (decimal.Round(parameter.Value, 6) != parameter.Value || Math.Abs(parameter.Value) > MaximumReadingMagnitude)
            {
                throw new ValidationAppException("A reading value must fit decimal(18,6).", "value");
            }
        }
    }

    private static void ValidateRequired(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException($"{field} is required.", field);
        }
        ValidateLength(value, field, maximumLength);
    }

    private static void ValidateLength(string? value, string field, int maximumLength)
    {
        if (value?.Trim().Length > maximumLength)
        {
            throw new ValidationAppException($"{field} cannot exceed {maximumLength} characters.", field);
        }
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

    private void AddMilkTestEvent(
        MilkTest milkTest,
        Guid deliveryId,
        string eventType,
        string eventKey,
        string message,
        DateTime occurredAtUtc,
        IReadOnlyDictionary<string, string>? additionalVariables = null)
    {
        var variables = new Dictionary<string, string>
        {
            ["milkTestId"] = milkTest.PublicId.ToString(),
            ["deliveryId"] = deliveryId.ToString(),
            ["message"] = message
        };
        if (additionalVariables is not null)
        {
            foreach (var variable in additionalVariables)
            {
                variables[variable.Key] = variable.Value;
            }
        }

        notificationEventWriter.Add(new NotificationEventRequest(
            milkTest.CustomerId,
            eventType,
            eventKey,
            variables,
            $"/deliveries/{deliveryId}/milk-test",
            occurredAtUtc));
    }

    private void AddAudit(
        long userId,
        string action,
        Guid milkTestId,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime createdAtUtc) =>
        dbContext.AuditLogs.Add(new AuditLog(
            userId,
            action,
            "MilkTest",
            milkTestId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
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
            throw new ConflictException(exception.Message);
        }
    }

    private static StaffMilkTestResult ToStaffResult(MilkTest milkTest, Guid deliveryId) => new(
        milkTest.PublicId,
        deliveryId,
        milkTest.Status,
        milkTest.CustomerDecision,
        milkTest.RequestedAtUtc,
        milkTest.CompletedAtUtc,
        milkTest.StaffRemarks,
        milkTest.ConfirmedAtUtc,
        milkTest.RejectedAtUtc,
        milkTest.CustomerRemarks,
        milkTest.Parameters
            .OrderBy(x => x.Code)
            .Select(x => new MilkTestParameterResult(x.Code, x.Name, x.Value, x.Unit))
            .ToArray(),
        milkTest.Images
            .OrderBy(x => x.UploadedAtUtc)
            .Select(x => ToImageResult(milkTest.PublicId, x))
            .ToArray());

    private static CustomerMilkTestResult ToCustomerResult(MilkTest milkTest, Guid deliveryId) => new(
        milkTest.PublicId,
        deliveryId,
        milkTest.Status,
        milkTest.CustomerDecision,
        milkTest.RequestedAtUtc,
        milkTest.CompletedAtUtc,
        milkTest.ConfirmedAtUtc,
        milkTest.RejectedAtUtc,
        milkTest.CustomerRemarks,
        milkTest.Status == MilkTestStatus.Completed
            ? milkTest.Images
                .OrderBy(x => x.UploadedAtUtc)
                .Select(x => ToImageResult(milkTest.PublicId, x))
                .ToArray()
            : []);

    private static MilkTestImageResult ToImageResult(Guid milkTestId, MilkTestImage image) => new(
        image.PublicId,
        image.FileName,
        image.ContentType,
        image.FileSize,
        image.UploadedAtUtc,
        $"/api/v1/milk-tests/{milkTestId}/images/{image.PublicId}/content");
}
