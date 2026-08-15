namespace DoodhDirect.Application.Common;

public sealed class UnauthorizedAppException(string message = "Authentication failed.")
    : AppException(message, "UNAUTHORIZED", 401);

public sealed class ForbiddenAppException(string message = "You are not authorized to perform this action.")
    : AppException(message, "FORBIDDEN", 403);

public sealed class RateLimitAppException(string message = "Too many requests. Please try again later.")
    : AppException(message, "RATE_LIMITED", 429);
