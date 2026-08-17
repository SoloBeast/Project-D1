using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Cameras;

public sealed class CameraStreamOptions
{
    public const string SectionName = "CameraStreams";
    public const string UnconfiguredProvider = "Unconfigured";
    public const string DevelopmentMockProvider = "DevelopmentMock";

    [Required]
    [MaxLength(80)]
    public string Provider { get; init; } = UnconfiguredProvider;

    [Url]
    public string? DevelopmentHlsPlaybackUrl { get; init; }

    [Range(1, 60)]
    public int DescriptorLifetimeMinutes { get; init; } = 5;

    public bool IsDevelopmentMock => string.Equals(
        Provider,
        DevelopmentMockProvider,
        StringComparison.OrdinalIgnoreCase);
}
