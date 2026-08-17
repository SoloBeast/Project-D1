using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Cameras;

public enum CameraStreamProtocol
{
    Hls = 1,
    WebRtc = 2
}

public sealed class Camera : AuditableEntity
{
    private Camera() { }

    public Camera(
        long branchId,
        string internalIdentifier,
        string displayName,
        bool isPublic,
        int displayOrder)
    {
        Update(branchId, internalIdentifier, displayName, isPublic, displayOrder);
        IsActive = true;
    }

    public long BranchId { get; private set; }
    public string InternalIdentifier { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsPublic { get; private set; }
    public bool IsActive { get; private set; }
    public int DisplayOrder { get; private set; }

    public CameraStream Stream { get; private set; } = null!;

    public void Update(
        long branchId,
        string internalIdentifier,
        string displayName,
        bool isPublic,
        int displayOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);

        BranchId = branchId;
        InternalIdentifier = internalIdentifier.Trim().ToUpperInvariant();
        DisplayName = displayName.Trim();
        IsPublic = isPublic;
        DisplayOrder = displayOrder;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public void MakePublic() => IsPublic = true;

    public void MakePrivate() => IsPublic = false;

    public void Reorder(int displayOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);
        DisplayOrder = displayOrder;
    }
}

public sealed class CameraStream : AuditableEntity
{
    private CameraStream() { }

    public CameraStream(
        long cameraId,
        CameraStreamProtocol protocol,
        string providerCode,
        string providerStreamReference)
    {
        CameraId = cameraId;
        Update(protocol, providerCode, providerStreamReference);
    }

    public long CameraId { get; private set; }
    public CameraStreamProtocol Protocol { get; private set; }
    public string ProviderCode { get; private set; } = string.Empty;
    public string ProviderStreamReference { get; private set; } = string.Empty;

    public Camera Camera { get; private set; } = null!;

    public void Update(
        CameraStreamProtocol protocol,
        string providerCode,
        string providerStreamReference)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStreamReference);

        Protocol = protocol;
        ProviderCode = providerCode.Trim().ToUpperInvariant();
        ProviderStreamReference = providerStreamReference.Trim();
    }
}
