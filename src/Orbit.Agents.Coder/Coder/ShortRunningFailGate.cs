using System.Collections.Concurrent;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Cap Short-Running Evaluator→Generator FAIL loops <b>per AG-UI run</b> (not per session).
/// Scope uses <see cref="OrbitRunContext.RunId"/> so a later user turn in the same session starts at 0.
/// Advisory for the model (MAF cannot block handoff tools); hard outer stop is message-count termination.
/// </summary>
public static class ShortRunningFailGate
{
    /// <summary>How many FAIL→generator handoffs are allowed before forcing user/router.</summary>
    public const int MaxGeneratorHandoffsAfterFail = 2;

    private static readonly ConcurrentDictionary<string, int> FailCycles = new(StringComparer.Ordinal);

    /// <summary>Prefer RunId (one user turn); SessionId only if RunId missing; else local.</summary>
    private static string Key =>
        !string.IsNullOrWhiteSpace(OrbitRunContext.RunId) ? "r:" + OrbitRunContext.RunId
        : !string.IsNullOrWhiteSpace(OrbitRunContext.SessionId) ? "s:" + OrbitRunContext.SessionId
        : "local";

    public static string Status()
    {
        var n = FailCycles.TryGetValue(Key, out var c) ? c : 0;
        if (n >= MaxGeneratorHandoffsAfterFail)
        {
            return
                $"short_fail_cycles={n}/{MaxGeneratorHandoffsAfterFail} (scope={Key}). "
                + "Do NOT handoff coder_generator. ask_user_confirm to continue, else handoff coder_router.";
        }

        return
            $"short_fail_cycles={n}/{MaxGeneratorHandoffsAfterFail} (scope={Key}). "
            + "On VERDICT FAIL you may handoff coder_generator after note_short_fail.";
    }

    /// <summary>Call once per FAIL before handoff to generator.</summary>
    public static string NoteFail()
    {
        var n = FailCycles.AddOrUpdate(Key, 1, static (_, c) => c + 1);
        if (n > MaxGeneratorHandoffsAfterFail)
        {
            return
                $"FAIL cycle {n}: BLOCKED further coder_generator handoffs. "
                + "ask_user_confirm whether to reset the cycle; if no → handoff coder_router.";
        }

        return
            $"FAIL cycle {n}/{MaxGeneratorHandoffsAfterFail} recorded. "
            + (n == MaxGeneratorHandoffsAfterFail
                ? "This is the last allowed generator retry — next FAIL must go to user/router."
                : "You may handoff coder_generator now.");
    }

    public static string NotePass()
    {
        FailCycles.TryRemove(Key, out _);
        return "short_fail_cycles cleared (PASS).";
    }

    /// <summary>User approved another repair round after the cap.</summary>
    public static string ResetAfterUserOk()
    {
        FailCycles[Key] = 0;
        return "short_fail_cycles reset to 0 after user confirmation.";
    }
}
