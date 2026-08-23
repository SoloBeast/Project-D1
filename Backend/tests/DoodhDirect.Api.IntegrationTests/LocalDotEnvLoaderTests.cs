using System.Text;
using Microsoft.Extensions.Configuration;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class LocalDotEnvLoaderTests
{
    [Fact]
    public void Development_LoadsGeocodingProviderAndApiKeyFromDotEnv()
    {
        using var fixture = DotEnvFixture.Create(
            "ADDRESS_GEOCODING__PROVIDER=Google",
            "ADDRESS_GEOCODING__APIKEY=test-geocoding-key");
        var configuration = CreateConfiguration();

        LocalDotEnvLoader.Load(configuration, fixture.DirectoryPath, isDevelopment: true);

        Assert.Equal("Google", configuration["AddressGeocoding:Provider"]);
        Assert.Equal("test-geocoding-key", configuration["AddressGeocoding:ApiKey"]);
    }

    [Fact]
    public void Development_MissingGeocodingApiKeyPreservesExistingConfiguration()
    {
        using var fixture = DotEnvFixture.Create("ADDRESS_GEOCODING__PROVIDER=Google");
        var configuration = CreateConfiguration(("AddressGeocoding:ApiKey", string.Empty));

        LocalDotEnvLoader.Load(configuration, fixture.DirectoryPath, isDevelopment: true);

        Assert.Equal(string.Empty, configuration["AddressGeocoding:ApiKey"]);
    }

    [Fact]
    public void Development_DoesNotPrintOrLogGeocodingApiKey()
    {
        using var fixture = DotEnvFixture.Create("ADDRESS_GEOCODING__APIKEY=test-geocoding-key");
        var configuration = CreateConfiguration();
        var output = new StringBuilder();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            using var writer = new StringWriter(output);
            Console.SetOut(writer);
            Console.SetError(writer);

            LocalDotEnvLoader.Load(configuration, fixture.DirectoryPath, isDevelopment: true);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.DoesNotContain("test-geocoding-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_DoesNotReadRepositoryDotEnv()
    {
        using var fixture = DotEnvFixture.Create(
            "ADDRESS_GEOCODING__PROVIDER=Google",
            "ADDRESS_GEOCODING__APIKEY=test-geocoding-key");
        var configuration = CreateConfiguration();

        LocalDotEnvLoader.Load(configuration, fixture.DirectoryPath, isDevelopment: false);

        Assert.Null(configuration["AddressGeocoding:Provider"]);
        Assert.Null(configuration["AddressGeocoding:ApiKey"]);
    }

    private static ConfigurationManager CreateConfiguration(
        params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.ToDictionary(
            value => value.Key,
            value => (string?)value.Value));
        return configuration;
    }

    private sealed class DotEnvFixture : IDisposable
    {
        private DotEnvFixture(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static DotEnvFixture Create(params string[] lines)
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "DoodhDirect-LocalDotEnvTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            File.WriteAllLines(
                Path.Combine(directoryPath, ".env"),
                lines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new DotEnvFixture(directoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
