namespace Orbit.Shell.Services;

/// <summary>
/// Fire-and-forget UI handlers must not become process-killing unobserved faults.
/// Prefer <see cref="Run"/> from <c>async void</c> event handlers.
/// </summary>
internal static class UiAsync
{
    public static async void Run(Func<Task> work, Action<Exception>? onError = null)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShellFaultLog.Error(ex, "Unhandled UI async fault");
            try
            {
                onError?.Invoke(ex);
            }
            catch (Exception sinkEx)
            {
                ShellFaultLog.Error(sinkEx, "UI async error sink failed");
            }
        }
    }
}
