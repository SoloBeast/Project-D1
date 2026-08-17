using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Cameras;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Cameras;

public sealed class DevelopmentCameraStreamGateway(
    IOptions<CameraStreamOptions> options,
    IClock clock) : ICameraStreamGateway
{
    private readonly CameraStreamOptions _options = options.Value;

    public bool CanIssue(CameraStreamProtocol protocol, string providerCode) =>
        _options.IsDevelopmentMock
        && protocol == CameraStreamProtocol.Hls
        && string.Equals(
            providerCode,
            CameraStreamOptions.DevelopmentMockProvider,
            StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(
            _options.DevelopmentHlsPlaybackUrl,
            UriKind.Absolute,
            out var playbackUri)
        && playbackUri.Scheme == Uri.UriSchemeHttps;

    public Task<bool> IsAvailableAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(CanIssue(request.Protocol, request.ProviderCode));

    public Task<CameraStreamDescriptor> IssueAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanIssue(request.Protocol, request.ProviderCode)
            || !Uri.TryCreate(
                _options.DevelopmentHlsPlaybackUrl,
                UriKind.Absolute,
                out var playbackUri))
        {
            throw new CameraStreamUnavailableException();
        }

        return Task.FromResult(new CameraStreamDescriptor(
            CameraStreamProtocol.Hls,
            playbackUri,
            clock.UtcNow.AddMinutes(_options.DescriptorLifetimeMinutes),
            true));
    }
}

public sealed class UnconfiguredCameraStreamGateway : ICameraStreamGateway
{
    public bool CanIssue(CameraStreamProtocol protocol, string providerCode) => false;

    public Task<bool> IsAvailableAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<CameraStreamDescriptor> IssueAsync(
        CameraStreamRequest request,
        CancellationToken cancellationToken) =>
        throw new CameraStreamUnavailableException();
}
