namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Options controlling application startup seeding.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "SeedOptions";

    /// <summary>
    /// When true, the API runs the development seed services at startup
    /// (identity roles/permissions, development users, catalogue, number series
    /// and notification templates). When false (the default) no seeding happens
    /// automatically and environments must be provisioned out-of-band.
    /// </summary>
    public bool EnableDevelopmentSeeds { get; init; }
}
