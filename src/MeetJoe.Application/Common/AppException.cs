namespace MeetJoe.Application.Common;

/// <summary>An expected failure with a client-safe message and an HTTP status.</summary>
public sealed class AppException : Exception
{
    private AppException(string code, string message, int statusCode) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }

    /// <summary>
    /// Also used when the caller lacks access to something that exists, so responses do not reveal
    /// which ids are real.
    /// </summary>
    public static AppException NotFound(string what) => new("not_found", $"{what} was not found.", 404);

    public static AppException Forbidden(string message) => new("forbidden", message, 403);

    public static AppException Validation(string message) => new("validation", message, 400);

    public static AppException Conflict(string message) => new("conflict", message, 409);

    public static AppException Unavailable(string message) => new("unavailable", message, 503);
}
