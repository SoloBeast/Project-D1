using DoodhDirect.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Verifies Data Protection key-directory resolution and file-system
/// persistence. Never asserts key material or protected payload contents —
/// only directory selection, key-file creation, and protect/unprotect
/// round-trips across a simulated restart (provider recreated).
/// </summary>
public sealed class DataProtectionKeyDirectoryTests
{
    [Fact]
    public void Explicit_value_always_wins()
    {
        Assert.Equal(
            @"D:\Custom\Keys",
            DataProtectionKeyDirectory.Resolve(@"D:\Custom\Keys", isDevelopment: true));
        Assert.Equal(
            @"D:\Custom\Keys",
            DataProtectionKeyDirectory.Resolve(@"D:\Custom\Keys", isDevelopment: false));
    }

    [Fact]
    public void Development_empty_uses_local_app_data_fallback()
    {
        var resolved = DataProtectionKeyDirectory.Resolve("", isDevelopment: true);

        Assert.NotNull(resolved);
        Assert.Contains("DoodhDirect", resolved);
        Assert.Contains("DataProtection-Keys", resolved);
        Assert.NotEqual(DataProtectionKeyDirectory.ProductionDefault, resolved);
    }

    [Fact]
    public void Non_development_empty_uses_machine_wide_persistent_default()
    {
        Assert.Equal(
            DataProtectionKeyDirectory.ProductionDefault,
            DataProtectionKeyDirectory.Resolve("", isDevelopment: false));
        Assert.Equal(
            DataProtectionKeyDirectory.ProductionDefault,
            DataProtectionKeyDirectory.Resolve(null, isDevelopment: false));
        Assert.Equal(
            DataProtectionKeyDirectory.ProductionDefault,
            DataProtectionKeyDirectory.Resolve("   ", isDevelopment: false));
    }

    [Fact]
    public void Production_default_is_outside_the_repository()
    {
        Assert.Equal(
            @"C:\ProgramData\DoodhDirect\DataProtection-Keys",
            DataProtectionKeyDirectory.ProductionDefault);
    }

    [Fact]
    public void File_system_ring_survives_provider_recreation()
    {
        // Simulates an API restart: two independently built providers over
        // the same directory must share the key ring. Uses an isolated
        // temp directory so tests never touch the machine-wide location.
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DoodhDirect-DataProtection-Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            const string purpose = "DoodhDirect.Notifications.PushToken.v1";
            const string plaintext = "test-push-token-placeholder";

            var servicesBefore = new ServiceCollection();
            servicesBefore.AddDataProtection()
                .SetApplicationName("DoodhDirect")
                .PersistKeysToFileSystem(new DirectoryInfo(directory));
            var providerBefore = servicesBefore.BuildServiceProvider()
                .GetRequiredService<IDataProtectionProvider>();
            var protectedPayload = providerBefore
                .CreateProtector(purpose)
                .Protect(plaintext);

            // Restart: drop the provider entirely and build a fresh one.
            var servicesAfter = new ServiceCollection();
            servicesAfter.AddDataProtection()
                .SetApplicationName("DoodhDirect")
                .PersistKeysToFileSystem(new DirectoryInfo(directory));
            var providerAfter = servicesAfter.BuildServiceProvider()
                .GetRequiredService<IDataProtectionProvider>();
            var roundTripped = providerAfter
                .CreateProtector(purpose)
                .Unprotect(protectedPayload);

            Assert.Equal(plaintext, roundTripped);
            Assert.NotEmpty(Directory.GetFiles(directory, "key-*.xml"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
