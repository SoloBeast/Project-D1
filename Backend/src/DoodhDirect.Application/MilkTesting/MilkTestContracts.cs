using DoodhDirect.Domain.MilkTesting;

namespace DoodhDirect.Application.MilkTesting;

public sealed record MilkTestActor(
    long UserId,
    IReadOnlySet<long> BranchIds,
    bool HasGlobalAccess);

public sealed record MilkTestParameterRequest(
    string Code,
    string Name,
    decimal Value,
    string Unit);

public sealed record CompleteMilkTestRequest(
    IReadOnlyCollection<MilkTestParameterRequest> Parameters,
    string? Remarks);

public sealed record DecideMilkTestRequest(string? Remarks);

public sealed record MilkTestParameterResult(
    string Code,
    string Name,
    decimal Value,
    string Unit);

public sealed record MilkTestImageResult(
    Guid ImageId,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UploadedAt,
    string ContentPath);

public sealed record StaffMilkTestResult(
    Guid MilkTestId,
    Guid DeliveryId,
    MilkTestStatus Status,
    MilkTestCustomerDecision CustomerDecision,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    string? StaffRemarks,
    DateTime? ConfirmedAt,
    DateTime? RejectedAt,
    string? CustomerRemarks,
    IReadOnlyCollection<MilkTestParameterResult> Parameters,
    IReadOnlyCollection<MilkTestImageResult> Images);

public sealed record CustomerMilkTestResult(
    Guid MilkTestId,
    Guid DeliveryId,
    MilkTestStatus Status,
    MilkTestCustomerDecision CustomerDecision,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    DateTime? ConfirmedAt,
    DateTime? RejectedAt,
    string? CustomerRemarks,
    IReadOnlyCollection<MilkTestImageResult> Images);

public sealed record StoredMediaResult(
    string StorageKey,
    string ContentType,
    long FileSize);

public sealed record StoredMediaContent(
    Stream Content,
    string ContentType,
    long FileSize) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IMediaStorage
{
    Task<StoredMediaResult> SaveAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    Task<StoredMediaContent> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(
        string storageKey,
        CancellationToken cancellationToken);
}

public interface IMilkTestImageValidator
{
    long MaximumFileSize { get; }

    Task<ValidatedMilkTestImage> ValidateAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);
}

public sealed record ValidatedMilkTestImage(
    string FileName,
    string ContentType,
    long FileSize,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IMilkTestService
{
    Task<CustomerMilkTestResult> RequestAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken);

    Task<CustomerMilkTestResult?> GetForCustomerAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken);

    Task<StaffMilkTestResult?> GetForStaffAsync(
        MilkTestActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken);

    Task<MilkTestImageResult> UploadImageAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    Task<StaffMilkTestResult> DeleteImageAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Guid imageId,
        CancellationToken cancellationToken);

    Task<MilkTestImageResult> ReplaceImageAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Guid imageId,
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);

    Task<StoredMediaContent> OpenImageAsync(
        MilkTestActor actor,
        Guid milkTestId,
        Guid imageId,
        CancellationToken cancellationToken);

    Task<StaffMilkTestResult> CompleteAsync(
        MilkTestActor actor,
        Guid milkTestId,
        CompleteMilkTestRequest request,
        CancellationToken cancellationToken);

    Task<CustomerMilkTestResult> ConfirmAsync(
        MilkTestActor actor,
        Guid milkTestId,
        DecideMilkTestRequest request,
        CancellationToken cancellationToken);

    Task<CustomerMilkTestResult> RejectAsync(
        MilkTestActor actor,
        Guid milkTestId,
        DecideMilkTestRequest request,
        CancellationToken cancellationToken);
}
