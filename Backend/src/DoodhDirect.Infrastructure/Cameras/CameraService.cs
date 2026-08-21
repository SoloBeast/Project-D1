using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Cameras;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Cameras;

public sealed class CameraService(
    DoodhDirectDbContext dbContext,
    ICameraStreamGateway streamGateway,
    IIndiaTimeProvider timeProvider) : ICameraService
{
    public async Task<IReadOnlyCollection<PublicCameraResult>> GetPublicAsync(
        CancellationToken cancellationToken)
    {
        var cameras = await dbContext.Cameras
            .AsNoTracking()
            .Include(x => x.Stream)
            .Where(x => x.IsActive && x.IsPublic)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.DisplayName)
            .Select(x => new PublicCameraMetadata(
                x.PublicId,
                x.DisplayName,
                x.DisplayOrder,
                x.Stream.Protocol,
                x.Stream.ProviderCode,
                x.Stream.ProviderStreamReference))
            .ToArrayAsync(cancellationToken);

        var results = await Task.WhenAll(cameras.Select(async camera =>
        {
            var request = camera.ToStreamRequest();
            var isAvailable = streamGateway.CanIssue(request.Protocol, request.ProviderCode)
                && await streamGateway.IsAvailableAsync(request, cancellationToken);
            return new PublicCameraResult(
                camera.CameraId,
                camera.DisplayName,
                camera.DisplayOrder,
                isAvailable);
        }));
        return results;
    }

    public async Task<PublicCameraStreamResult> GetPublicStreamAsync(
        Guid cameraId,
        CancellationToken cancellationToken)
    {
        var camera = await dbContext.Cameras
            .AsNoTracking()
            .Include(x => x.Stream)
            .Where(x => x.PublicId == cameraId && x.IsActive && x.IsPublic)
            .Select(x => new PublicCameraMetadata(
                x.PublicId,
                x.DisplayName,
                x.DisplayOrder,
                x.Stream.Protocol,
                x.Stream.ProviderCode,
                x.Stream.ProviderStreamReference))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("The public camera was not found.");

        var request = camera.ToStreamRequest();
        if (!streamGateway.CanIssue(request.Protocol, request.ProviderCode)
            || !await streamGateway.IsAvailableAsync(request, cancellationToken))
        {
            throw new CameraStreamUnavailableException();
        }

        return new PublicCameraStreamResult(
            camera.CameraId,
            camera.DisplayName,
            await streamGateway.IssueAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyCollection<ManagedCameraResult>> GetManagedAsync(
        CameraActor actor,
        long? branchId,
        CancellationToken cancellationToken)
    {
        if (branchId is <= 0)
        {
            throw new ValidationAppException("branchId must be positive.", "branchId");
        }
        if (branchId.HasValue)
        {
            EnsureBranchAccess(actor, branchId.Value);
        }

        var cameras = dbContext.Cameras.AsNoTracking();
        if (!actor.HasGlobalAccess)
        {
            cameras = cameras.Where(x => actor.BranchIds.Contains(x.BranchId));
        }
        if (branchId.HasValue)
        {
            cameras = cameras.Where(x => x.BranchId == branchId.Value);
        }

        var query =
            from camera in cameras
            join branch in dbContext.Branches.AsNoTracking() on camera.BranchId equals branch.Id
            orderby branch.Name, camera.DisplayOrder, camera.DisplayName
            select new ManagedCameraResult(
                camera.PublicId,
                camera.BranchId,
                branch.Name,
                camera.InternalIdentifier,
                camera.DisplayName,
                camera.IsPublic,
                camera.IsActive,
                camera.DisplayOrder,
                camera.Stream.Protocol,
                camera.Stream.ProviderCode,
                camera.Stream.ProviderStreamReference,
                camera.CreatedAt,
                camera.UpdatedAt);

        return await query.ToArrayAsync(cancellationToken);
    }

    public async Task<ManagedCameraResult> CreateAsync(
        CameraActor actor,
        CreateCameraRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        EnsureBranchAccess(actor, request.BranchId);

        ManagedCameraResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var branch = await dbContext.Branches.SingleOrDefaultAsync(
                x => x.Id == request.BranchId,
                cancellationToken) ?? throw new NotFoundException("The branch was not found.");
            await EnsureIdentifierAvailableAsync(request.BranchId, request.InternalIdentifier, null, cancellationToken);

            var camera = Mutate(() => new Camera(
                request.BranchId,
                request.InternalIdentifier,
                request.DisplayName,
                request.IsPublic,
                request.DisplayOrder));
            dbContext.Cameras.Add(camera);
            await dbContext.SaveChangesAsync(cancellationToken);

            var stream = Mutate(() => new CameraStream(
                camera.Id,
                request.Protocol,
                request.ProviderCode,
                request.ProviderStreamReference));
            dbContext.CameraStreams.Add(stream);
            AddAudit(actor.UserId, "CAMERA.CREATE", camera.PublicId, null, new
            {
                camera.BranchId,
                camera.InternalIdentifier,
                camera.DisplayName,
                camera.IsPublic,
                camera.IsActive,
                camera.DisplayOrder,
                stream.Protocol,
                stream.ProviderCode
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToManagedResult(camera, stream, branch.Name);
        }, cancellationToken);
        return result!;
    }

    public async Task<ManagedCameraResult> UpdateAsync(
        CameraActor actor,
        Guid cameraId,
        UpdateCameraRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        EnsureBranchAccess(actor, request.BranchId);

        ManagedCameraResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var camera = await dbContext.Cameras
                .Include(x => x.Stream)
                .SingleOrDefaultAsync(x => x.PublicId == cameraId, cancellationToken)
                ?? throw new NotFoundException("The camera was not found.");
            EnsureBranchAccess(actor, camera.BranchId);

            var branch = await dbContext.Branches.SingleOrDefaultAsync(
                x => x.Id == request.BranchId,
                cancellationToken) ?? throw new NotFoundException("The branch was not found.");
            await EnsureIdentifierAvailableAsync(
                request.BranchId,
                request.InternalIdentifier,
                camera.Id,
                cancellationToken);

            var oldValue = new
            {
                camera.BranchId,
                camera.InternalIdentifier,
                camera.DisplayName,
                camera.IsPublic,
                camera.IsActive,
                camera.DisplayOrder,
                camera.Stream.Protocol,
                camera.Stream.ProviderCode
            };

            Mutate(() => camera.Update(
                request.BranchId,
                request.InternalIdentifier,
                request.DisplayName,
                request.IsPublic,
                request.DisplayOrder));
            if (request.IsActive)
            {
                camera.Activate();
            }
            else
            {
                camera.Deactivate();
            }
            Mutate(() => camera.Stream.Update(
                request.Protocol,
                request.ProviderCode,
                request.ProviderStreamReference));

            AddAudit(actor.UserId, "CAMERA.UPDATE", camera.PublicId, oldValue, new
            {
                camera.BranchId,
                camera.InternalIdentifier,
                camera.DisplayName,
                camera.IsPublic,
                camera.IsActive,
                camera.DisplayOrder,
                camera.Stream.Protocol,
                camera.Stream.ProviderCode
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToManagedResult(camera, camera.Stream, branch.Name);
        }, cancellationToken);
        return result!;
    }

    private async Task EnsureIdentifierAvailableAsync(
        long branchId,
        string internalIdentifier,
        long? excludedCameraId,
        CancellationToken cancellationToken)
    {
        var normalizedIdentifier = internalIdentifier.Trim().ToUpperInvariant();
        if (await dbContext.Cameras.AnyAsync(
            x => x.BranchId == branchId
                 && x.InternalIdentifier == normalizedIdentifier
                 && (!excludedCameraId.HasValue || x.Id != excludedCameraId.Value),
            cancellationToken))
        {
            throw new ConflictException("A camera with this internal identifier already exists in the branch.");
        }
    }

    private static void EnsureBranchAccess(CameraActor actor, long branchId)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(branchId))
        {
            throw new NotFoundException("The camera or branch was not found.");
        }
    }

    private static void Validate(CreateCameraRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(
            request.BranchId,
            request.InternalIdentifier,
            request.DisplayName,
            request.DisplayOrder,
            request.Protocol,
            request.ProviderCode,
            request.ProviderStreamReference);
    }

    private static void Validate(UpdateCameraRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommon(
            request.BranchId,
            request.InternalIdentifier,
            request.DisplayName,
            request.DisplayOrder,
            request.Protocol,
            request.ProviderCode,
            request.ProviderStreamReference);
    }

    private static void ValidateCommon(
        long branchId,
        string internalIdentifier,
        string displayName,
        int displayOrder,
        CameraStreamProtocol protocol,
        string providerCode,
        string providerStreamReference)
    {
        if (branchId <= 0)
        {
            throw new ValidationAppException("branchId must be positive.", "branchId");
        }
        ValidateRequired(internalIdentifier, "internalIdentifier", 100);
        ValidateRequired(displayName, "displayName", 160);
        if (displayOrder < 0)
        {
            throw new ValidationAppException("displayOrder cannot be negative.", "displayOrder");
        }
        if (!Enum.IsDefined(protocol))
        {
            throw new ValidationAppException("protocol is invalid.", "protocol");
        }
        ValidateRequired(providerCode, "providerCode", 80);
        ValidateRequired(providerStreamReference, "providerStreamReference", 240);
        if (providerStreamReference.IndexOfAny([':', '?', '#', '@', '\\']) >= 0)
        {
            throw new ValidationAppException(
                "providerStreamReference must be an opaque non-secret reference, not a URL, address, or credential.",
                "providerStreamReference");
        }
    }

    private static void ValidateRequired(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException($"{field} is required.", field);
        }
        if (value.Trim().Length > maximumLength)
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

    private void AddAudit(long userId, string action, Guid cameraId, object? oldValue, object newValue) =>
        dbContext.AuditLogs.Add(new AuditLog(
            userId,
            action,
            "Camera",
            cameraId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            JsonSerializer.Serialize(newValue),
            null,
            null,
            null,
            timeProvider.Now));

    private static T Mutate<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
    }

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
    }

    private static ManagedCameraResult ToManagedResult(Camera camera, CameraStream stream, string branchName) => new(
        camera.PublicId,
        camera.BranchId,
        branchName,
        camera.InternalIdentifier,
        camera.DisplayName,
        camera.IsPublic,
        camera.IsActive,
        camera.DisplayOrder,
        stream.Protocol,
        stream.ProviderCode,
        stream.ProviderStreamReference,
        camera.CreatedAt,
        camera.UpdatedAt);

    private sealed record PublicCameraMetadata(
        Guid CameraId,
        string DisplayName,
        int DisplayOrder,
        CameraStreamProtocol Protocol,
        string ProviderCode,
        string ProviderStreamReference)
    {
        public CameraStreamRequest ToStreamRequest() => new(
            CameraId,
            Protocol,
            ProviderCode,
            ProviderStreamReference);
    }
}
