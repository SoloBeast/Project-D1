using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.MilkTesting;

public sealed class LocalMediaStorage : IMediaStorage
{
    private readonly string _rootPath;

    public LocalMediaStorage(IOptions<MilkTestMediaOptions> options)
    {
        var configuredPath = options.Value.LocalRootPath;
        _rootPath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath));
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredMediaResult> SaveAsync(
        string storageKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var destinationPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            return new StoredMediaResult(storageKey, contentType, new FileInfo(destinationPath).Length);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    public Task<StoredMediaContent> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(storageKey);
        if (!File.Exists(path))
        {
            throw new NotFoundException("The test image content was not found.");
        }

        Stream content = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(new StoredMediaContent(content, "application/octet-stream", content.Length));
    }

    public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || Path.IsPathRooted(storageKey))
        {
            throw new ValidationAppException("The media storage key is invalid.");
        }

        var normalizedKey = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(Path.Combine(_rootPath, normalizedKey));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationAppException("The media storage key is invalid.");
        }

        return resolvedPath;
    }
}
