using Orbit.Contracts;

namespace Orbit.Runtime.Hosting;

/// <summary>Map exceptions / codes to structured JSON results for Shell.</summary>
internal static class OrbitApiResults
{
    public static IResult Json(int statusCode, string code, string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        Results.Json(OrbitApiError.Create(code, message, details), statusCode: statusCode);

    /// <summary>
    /// Map a caught exception to a JSON result. Unexpected faults are logged with the exception
    /// and reported as a generic 500 so internal details (paths, stack-y messages) stay in the log.
    /// </summary>
    public static IResult FromException(Exception ex, ILogger? logger = null, string? operation = null)
    {
        if (ex is OrbitApiException or KeyNotFoundException or ArgumentException or InvalidDataException
            or HttpRequestException or InvalidOperationException or NotSupportedException)
        {
            return FromException(ex);
        }

        logger?.LogError(ex, "Unhandled error in {Operation}", operation ?? "runtime request");
        return Json(
            StatusCodes.Status500InternalServerError,
            OrbitErrorCodes.InternalError,
            "internal_error: unexpected failure. See Runtime logs.",
            new Dictionary<string, string> { ["operation"] = operation ?? "runtime request" });
    }

    public static IResult FromException(Exception ex) =>
        ex switch
        {
            OrbitApiException api => Json(StatusFor(api.Code), api.Code, api.Message, api.Details),
            KeyNotFoundException knf => Json(
                StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, knf.Message),
            ArgumentException arg => Json(
                StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, arg.Message),
            InvalidDataException corrupt => Json(
                StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, corrupt.Message),
            HttpRequestException http => Json(
                StatusCodes.Status502BadGateway, OrbitErrorCodes.BadGateway, http.Message),
            InvalidOperationException ioe when TryLegacy(ioe.Message, out var code) =>
                Json(StatusFor(code), code, ioe.Message),
            InvalidOperationException ioe => Json(
                StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, ioe.Message),
            NotSupportedException nse => Json(
                StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, nse.Message),
            // Unexpected faults → generic 500 (prefer the logger overload so the fault is recorded).
            _ => Json(
                StatusCodes.Status500InternalServerError,
                OrbitErrorCodes.InternalError,
                "internal_error: unexpected failure. See Runtime logs.")
        };

    public static IResult Unauthorized() =>
        Json(
            StatusCodes.Status401Unauthorized,
            OrbitErrorCodes.Unauthorized,
            "Unauthorized. Start Orbit.Shell or set X-Orbit-Token.");

    private static bool TryLegacy(string message, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (var known in new[]
                 {
                     OrbitErrorCodes.ConfirmRequired,
                     OrbitErrorCodes.SessionBusy,
                     OrbitErrorCodes.WorkspaceMismatch,
                     OrbitErrorCodes.WorkspaceRequired,
                     OrbitErrorCodes.WorkspaceMissing,
                     OrbitErrorCodes.WorkspaceActiveLocked,
                     OrbitErrorCodes.SelectionRequired,
                 })
        {
            if (message.StartsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                code = known;
                return true;
            }
        }

        return false;
    }

    private static int StatusFor(string code) => code switch
    {
        OrbitErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        OrbitErrorCodes.ConfirmRequired => StatusCodes.Status409Conflict,
        OrbitErrorCodes.Conflict => StatusCodes.Status409Conflict,
        OrbitErrorCodes.SessionBusy => StatusCodes.Status409Conflict,
        OrbitErrorCodes.WorkspaceMismatch => StatusCodes.Status409Conflict,
        OrbitErrorCodes.NotFound => StatusCodes.Status404NotFound,
        OrbitErrorCodes.SelectionRequired => StatusCodes.Status400BadRequest,
        OrbitErrorCodes.WorkspaceRequired => StatusCodes.Status400BadRequest,
        OrbitErrorCodes.WorkspaceMissing => StatusCodes.Status400BadRequest,
        OrbitErrorCodes.WorkspaceActiveLocked => StatusCodes.Status400BadRequest,
        OrbitErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        OrbitErrorCodes.BadGateway => StatusCodes.Status502BadGateway,
        OrbitErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest
    };
}
