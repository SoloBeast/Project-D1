namespace DoodhDirect.Application.Catalogue;

/// <summary>
/// A validated product image upload. Reuses the proven milk-test validation
/// shape: the client filename is sanitized, the content type is detected from
/// magic bytes (the declared MIME is only cross-checked, never trusted), and
/// the stream is buffered under a hard size cap.
/// </summary>
public sealed record ValidatedProductImage(
    string FileName,
    string ContentType,
    long FileSize,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IProductImageValidator
{
    long MaximumFileSize { get; }

    Task<ValidatedProductImage> ValidateAsync(
        Stream content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken);
}
