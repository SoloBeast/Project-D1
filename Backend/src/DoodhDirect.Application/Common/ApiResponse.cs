namespace DoodhDirect.Application.Common;

public sealed record ApiError(string Code, string? Field, string Message);

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? Message,
    IReadOnlyCollection<ApiError> Errors)
{
    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new(true, data, message, []);

    public static ApiResponse<T> Failure(string message, params ApiError[] errors) =>
        new(false, default, message, errors);
}

public abstract class AppException(
    string message,
    string code,
    int statusCode,
    string? field = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? Field { get; } = field;
}

public sealed class ValidationAppException(string message, string? field = null)
    : AppException(message, "VALIDATION_ERROR", 400, field);

public sealed class BusinessRuleException(string message)
    : AppException(message, "BUSINESS_RULE", 422);

public sealed class NotFoundException(string message)
    : AppException(message, "NOT_FOUND", 404);

public sealed class ConflictException(string message)
    : AppException(message, "CONFLICT", 409);
