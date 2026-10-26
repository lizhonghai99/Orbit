namespace Orbit.Contracts;

/// <summary>Stable machine codes for Runtime → Shell API errors.</summary>
public static class OrbitErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string ConfirmRequired = "confirm_required";
    public const string Conflict = "conflict";
    public const string SessionBusy = "session_busy";
    public const string WorkspaceMismatch = "workspace_mismatch";
    public const string WorkspaceRequired = "workspace_required";
    public const string WorkspaceMissing = "workspace_missing";
    public const string WorkspaceActiveLocked = "workspace_active_locked";
    public const string SelectionRequired = "selection_required";
    public const string NotFound = "not_found";
    public const string BadRequest = "bad_request";
    public const string Forbidden = "forbidden";
    public const string BadGateway = "bad_gateway";
    public const string InternalError = "internal_error";
}
