using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.MilkTesting;

public sealed class MilkTestMediaOptions
{
    public const string SectionName = "MilkTestMedia";

    [Required]
    public string Provider { get; init; } = "Local";

    [Required]
    public string LocalRootPath { get; init; } = "App_Data/MilkTestMedia";

    [Range(1, 50)]
    public int MaximumFileSizeMegabytes { get; init; } = 10;
}
