using DoodhDirect.Domain.Cameras;

namespace DoodhDirect.Application.Cameras;

public sealed record CameraActor(
    long UserId,
    IReadOnlySet<long> BranchIds,
    bool HasGlobalAccess);

public sealed record CreateCameraRequest(
    long BranchId,
    string InternalIdentifier,
    string DisplayName,
    bool IsPublic,
    int DisplayOrder,
    CameraStreamProtocol Protocol,
    string ProviderCode,
    string ProviderStreamReference);

public sealed record UpdateCameraRequest(
    long BranchId,
    string InternalIdentifier,
    string DisplayName,
    bool IsPublic,
    bool IsActive,
    int DisplayOrder,
    CameraStreamProtocol Protocol,
    string ProviderCode,
    string ProviderStreamReference);

public sealed record PublicCameraResult(
    Guid CameraId,
    string DisplayName,
    int DisplayOrder,
    bool IsAvailable);

public sealed record CameraStreamDescriptor(
    CameraStreamProtocol Protocol,
    Uri PlaybackUri,
    DateTimeOffset ExpiresAtUtc,
    bool IsDevelopmentStream);

public sealed record PublicCameraStreamResult(
    Guid CameraId,
    string DisplayName,
    CameraStreamDescriptor Stream);

public sealed record ManagedCameraResult(
    Guid CameraId,
    long BranchId,
    string BranchName,
    string InternalIdentifier,
    string DisplayName,
    bool IsPublic,
    bool IsActive,
    int DisplayOrder,
    CameraStreamProtocol Protocol,
    string ProviderCode,
    string ProviderStreamReference,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CameraStreamRequest(
    Guid CameraId,
    CameraStreamProtocol Protocol,
    string ProviderCode,
    string ProviderStreamReference);

public interface ICameraStreamGateway
{
    bool CanIssue(CameraStreamProtocol protocol, string providerCode);

    Task<bool> IsAvailableAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken);

    Task<CameraStreamDescriptor> IssueAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken);
}

public interface ICameraService
{
    Task<IReadOnlyCollection<PublicCameraResult>> GetPublicAsync(
        CancellationToken cancellationToken);

    Task<PublicCameraStreamResult> GetPublicStreamAsync(
        Guid cameraId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ManagedCameraResult>> GetManagedAsync(
        CameraActor actor,
        long? branchId,
        CancellationToken cancellationToken);

    Task<ManagedCameraResult> CreateAsync(
        CameraActor actor,
        CreateCameraRequest request,
        CancellationToken cancellationToken);

    Task<ManagedCameraResult> UpdateAsync(
        CameraActor actor,
        Guid cameraId,
        UpdateCameraRequest request,
        CancellationToken cancellationToken);
}
