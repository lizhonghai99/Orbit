using System.Collections;
using System.Diagnostics;
using System.Text;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Guarded shell in the Coder workspace: command allowlist, workspace path fence,
/// secret-free env, and (on Windows) a Job Object process-tree cage.
/// File read/write/edit stay on <c>file_access_*</c>.
/// </summary>
public sealed class CoderShellTools
{
    /// <summary>Env vars safe to inherit into the child shell (no secrets).</summary>
    private static readonly HashSet<string> AllowedEnvNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "windir", "OS", "ComSpec",
        "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "USERNAME", "USERDOMAIN", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "LANG", "LC_ALL", "LC_CTYPE", "TERM", "COLORTERM",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "LOCALAPPDATA", "APPDATA",
        "DOTNET_ROOT", "DOTNET_HOST_PATH", "MSBuildSDKsPath",
        "GIT_INSTALL_ROOT"
    };

    private readonly string _workDir;

    public CoderShellTools(string workDir)
    {
        _workDir = Path.GetFullPath(workDir);
    }

    /// <summary>Process-start fallback root (when no ambient RunContext workspace).</summary>
    public string WorkspaceRoot => OrbitWorkspaceRoots.ResolveShellRoot(_workDir);

    public async Task<string> RunBashAsync(string command, CancellationToken cancellationToken = default)
    {
        // Fail closed: no bash → no shell tool. Never silently switch engines (the policy is bash-only).
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return "Error: bash unavailable — Git for Windows bash.exe not found. "
                   + "Install Git for Windows or set ORBIT_BASH_PATH to a bash.exe path. "
                   + "Orbit does not fall back to PowerShell because the command policy is bash-only.";
        }

        var workDir = OrbitWorkspaceRoots.ResolveShellRoot(_workDir);
        if (!CoderBashPolicy.TryValidate(command, workDir, out var policyError))
        {
            return $"Error: bash blocked — {policyError}";
        }

        try
        {
            var startInfo = BuildProcessStartInfo(bashPath, command, workDir);
            using var process = new Process { StartInfo = startInfo };

            ApplyEnvironmentWhitelist(process.StartInfo);
            // Keep real HOME/USERPROFILE so git can read ~/.gitconfig. Path escapes are blocked by CoderBashPolicy.

            using var job = WindowsJobObject.TryCreate(
                maxProcesses: ResolveJobMaxProcesses(),
                maxMemoryBytes: ResolveJobMaxMemoryBytes());
            process.Start();
            job?.TryAssign(process);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // best-effort drain after kill
                }

                // Await readers so Dispose does not leave UnobservedTaskException / truncated pipes.
                try
                {
                    await Task.WhenAny(
                            Task.WhenAll(stdoutTask, stderrTask),
                            Task.Delay(TimeSpan.FromSeconds(2)))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                return "Error: command timed out (60s).";
            }

            var output = ((await stdoutTask.ConfigureAwait(false)) + (await stderrTask.ConfigureAwait(false))).Trim();
            if (string.IsNullOrEmpty(output))
            {
                output = $"(no output, exit {process.ExitCode})";
            }

            return output.Length <= 50_000 ? output : output[..50_000] + "\n…(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Clear inherited env and re-add a whitelist so <c>bash("env")</c> cannot leak
    /// API keys / Runtime tokens into model-visible output.
    /// </summary>
    public static void ApplyEnvironmentWhitelist(ProcessStartInfo startInfo)
    {
        var keep = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key) || entry.Value is null)
            {
                continue;
            }

            if (IsSecretEnvName(key) || !AllowedEnvNames.Contains(key))
            {
                continue;
            }

            keep[key] = entry.Value.ToString();
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in keep)
        {
            startInfo.Environment[key] = value;
        }
    }

    public static bool IsSecretEnvName(string key)
    {
        var k = key.ToUpperInvariant();
        return k.Contains("API_KEY", StringComparison.Ordinal)
               || k.Contains("TOKEN", StringComparison.Ordinal)
               || k.Contains("SECRET", StringComparison.Ordinal)
               || k.Contains("PASSWORD", StringComparison.Ordinal)
               || k.Contains("CREDENTIAL", StringComparison.Ordinal)
               || k.StartsWith("ORBIT_LLM", StringComparison.Ordinal)
               || k.StartsWith("DEEPSEEK", StringComparison.Ordinal)
               || k.StartsWith("MOONSHOT", StringComparison.Ordinal)
               || k.StartsWith("OPENAI", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve the Git Bash used to execute commands. Override with <c>ORBIT_BASH_PATH</c>.
    /// Returns null when bash is unavailable — callers must fail closed.
    /// </summary>
    public static string? ResolveBashPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("ORBIT_BASH_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ProcessStartInfo BuildProcessStartInfo(string bashPath, string command, string workDir)
    {
        // Git Bash only. The policy (CoderBashPolicy) is written against bash semantics ($(), ``,
        // ${}, ~, /abs paths); executing the same string with PowerShell would validate one language
        // and run another, so there is deliberately no PowerShell fallback.
        // Use ArgumentList so Windows argv parsing cannot strip quotes; bash still expands
        // $()/` inside the -lc script — CoderBashPolicy must block those.
        var psi = new ProcessStartInfo
        {
            FileName = bashPath,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--norc");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        return psi;
    }

    /// <summary>Label for Agent <c>/health</c> — <c>git-bash</c>, or <c>none</c> when bash is missing.</summary>
    public static string DescribePreferredShell() =>
        ResolveBashPath() is null ? "none" : "git-bash";

    /// <summary>
    /// Legacy helper for tests: POSIX single-quote wrap. Prefer <see cref="BuildProcessStartInfo"/> /
    /// ArgumentList at runtime — Windows <c>Arguments</c> parsing can strip quotes before bash sees them.
    /// </summary>
    public static string QuoteBash(string command) =>
        "'" + command.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary><c>ORBIT_BASH_JOB_MEMORY_MB</c> (default 2048). Floor 256MB.</summary>
    public static long ResolveJobMaxMemoryBytes()
    {
        var raw = Environment.GetEnvironmentVariable("ORBIT_BASH_JOB_MEMORY_MB");
        if (int.TryParse(raw, out var mb) && mb > 0)
        {
            return Math.Max(256, mb) * 1024L * 1024L;
        }

        return 2048L * 1024 * 1024;
    }

    /// <summary><c>ORBIT_BASH_JOB_MAX_PROCESSES</c> (default 32). Floor 1.</summary>
    public static int ResolveJobMaxProcesses()
    {
        var raw = Environment.GetEnvironmentVariable("ORBIT_BASH_JOB_MAX_PROCESSES");
        if (int.TryParse(raw, out var n) && n > 0)
        {
            return Math.Max(1, n);
        }

        return 32;
    }
}
