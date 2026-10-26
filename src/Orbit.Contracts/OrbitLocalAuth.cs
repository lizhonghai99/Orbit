using System.Diagnostics;

namespace Orbit.Contracts;

/// <summary>
/// Localhost Runtime ↔ Shell shared secret. Runtime writes on start; Shell sends <see cref="HeaderName"/>.
/// </summary>
public static class OrbitLocalAuth
{
    public const string HeaderName = "X-Orbit-Token";

    public static string TokenDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit");

    /// <summary>Rolling Runtime logs (<c>runtime-YYYYMMDD.log</c>).</summary>
    public static string LogsDirectory => Path.Combine(TokenDirectory, "logs");

    public static string TokenFilePath => Path.Combine(TokenDirectory, "runtime.token");

    /// <summary>PID of the Runtime that currently owns the local auth slot.</summary>
    public static string TokenPidPath => Path.Combine(TokenDirectory, "runtime.pid");

    /// <summary>Create a fresh in-memory token (do not write until bind succeeds).</summary>
    public static string CreateToken() =>
        Convert.ToHexString(Guid.NewGuid().ToByteArray())
        + Convert.ToHexString(Guid.NewGuid().ToByteArray());

    /// <summary>
    /// Claim sole Runtime ownership via <see cref="TokenPidPath"/> (before orphan cleanup / token write).
    /// Stale pid files from crashed processes are reclaimed.
    /// </summary>
    public static bool TryClaimRuntimePid(out string? conflictMessage) =>
        TryClaimRuntimePid(TokenDirectory, out conflictMessage);

    /// <summary>
    /// Testable claim against an arbitrary directory (same <c>pid|startTicks</c> rules as production).
    /// </summary>
    public static bool TryClaimRuntimePid(string tokenDirectory, out string? conflictMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenDirectory);
        conflictMessage = null;
        Directory.CreateDirectory(tokenDirectory);
        var pidPath = Path.Combine(tokenDirectory, "runtime.pid");

        if (File.Exists(pidPath))
        {
            var raw = File.ReadAllText(pidPath).Trim();
            if (TryParsePidRecord(raw, out var existingPid, out var existingStartTicks)
                && existingPid != Environment.ProcessId
                && IsRecordedRuntimeAlive(existingPid, existingStartTicks))
            {
                conflictMessage =
                    $"Another Orbit Runtime (pid {existingPid}) already holds the local auth slot.";
                return false;
            }

            try
            {
                File.Delete(pidPath);
            }
            catch
            {
                // CreateNew below will still fail if the other process raced us.
            }
        }

        try
        {
            using var fs = new FileStream(
                pidPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(fs);
            // pid|startTicks: a recycled PID must not be mistaken for a live Runtime.
            writer.Write($"{Environment.ProcessId}|{TryGetStartTicks(Environment.ProcessId) ?? 0}");
        }
        catch (IOException)
        {
            conflictMessage = "Another Orbit Runtime claimed the local auth slot concurrently.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Claim pid (if not already) and persist the Shell token. Prefer calling
    /// <see cref="TryClaimRuntimePid"/> early, then <see cref="PersistToken"/> after bind.
    /// </summary>
    public static bool TryClaimShellToken(string token, out string? conflictMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!IsCurrentProcessClaimHolder() && !TryClaimRuntimePid(out conflictMessage))
        {
            return false;
        }

        conflictMessage = null;
        PersistToken(token);
        return true;
    }

    /// <summary>True when <see cref="TokenPidPath"/> names this process.</summary>
    public static bool IsCurrentProcessClaimHolder() =>
        IsCurrentProcessClaimHolder(TokenDirectory);

    /// <summary>Testable claim-holder check against an arbitrary directory.</summary>
    public static bool IsCurrentProcessClaimHolder(string tokenDirectory)
    {
        try
        {
            var pidPath = Path.Combine(tokenDirectory, "runtime.pid");
            if (!File.Exists(pidPath))
            {
                return false;
            }

            var raw = File.ReadAllText(pidPath).Trim();
            return TryParsePidRecord(raw, out var pid, out _) && pid == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Drop the pid claim when this process is the owner (best-effort on shutdown).</summary>
    public static void ReleaseTokenClaim() => ReleaseTokenClaim(TokenDirectory);

    /// <summary>Testable release against an arbitrary directory.</summary>
    public static void ReleaseTokenClaim(string tokenDirectory)
    {
        try
        {
            var pidPath = Path.Combine(tokenDirectory, "runtime.pid");
            if (!File.Exists(pidPath))
            {
                return;
            }

            var raw = File.ReadAllText(pidPath).Trim();
            if (TryParsePidRecord(raw, out var pid, out _) && pid == Environment.ProcessId)
            {
                File.Delete(pidPath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Persist token for Shell after the HTTP listener is up.</summary>
    public static void PersistToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(TokenDirectory);
        var path = TokenFilePath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, token.Trim());
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    /// <summary>Create a fresh token for this Runtime process and persist it for Shell.</summary>
    public static string IssueNewToken()
    {
        var token = CreateToken();
        PersistToken(token);
        return token;
    }

    public static string? TryReadToken()
    {
        try
        {
            var path = TokenFilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse <c>pid|startTicks</c> (legacy files contain the bare pid).</summary>
    internal static bool TryParsePidRecord(string raw, out int pid, out long? startTicks)
    {
        pid = 0;
        startTicks = null;
        var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out pid))
        {
            return false;
        }

        if (parts.Length == 2 && long.TryParse(parts[1], out var ticks) && ticks > 0)
        {
            startTicks = ticks;
        }

        return true;
    }

    /// <summary>
    /// The recorded process is still the Runtime that wrote the pid file. A live PID whose start
    /// time differs was recycled by an unrelated process, so the stale claim must be reclaimed.
    /// </summary>
    internal static bool IsRecordedRuntimeAlive(int pid, long? startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (startTicks is not { } expected)
            {
                // Legacy file: cannot distinguish a recycled PID, so keep the conservative answer.
                return true;
            }

            var actual = TryGetStartTicks(pid);
            return actual is null || actual == expected;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static long? TryGetStartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Access denied / exited: unknown start time — caller decides how conservative to be.
            return null;
        }
    }
}
