namespace FileKeep.Service;

/// <summary>
/// Runs the service's two long-lived halves (dashboard + scheduler) as
/// one unit: if either half faults — or simply ends — the other is
/// stopped and the failure propagates to the host.
///
/// SF-6: this used to be a plain <c>Task.WhenAll</c>, which only
/// completes when BOTH tasks end. A dashboard that faulted at startup
/// (e.g. the port was already bound) left the scheduler running
/// forever: the host reported RUNNING, backups proceeded with no
/// GUI/API, and the crash-reporting path never fired because the
/// host never died.
/// </summary>
public static class ServiceSupervisor
{
    public static Task RunTogetherAsync(
        string firstName, Func<CancellationToken, Task> first,
        string secondName, Func<CancellationToken, Task> second,
        CancellationToken ct) =>
        RunTogetherAsync(ct, (firstName, first), (secondName, second));

    /// <summary>
    /// Runs any number of long-lived halves as one unit: if any half
    /// faults — or simply ends — the others are stopped and the failure
    /// propagates to the host. The two-half overload above is the common
    /// case; the relay poller runs as a third half alongside the
    /// dashboard and the scheduler.
    /// </summary>
    public static async Task RunTogetherAsync(
        CancellationToken ct, params (string Name, Func<CancellationToken, Task> Run)[] halves)
    {
        if (halves.Length == 0)
            throw new ArgumentException("At least one half is required.", nameof(halves));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task[] tasks = halves.Select(h => h.Run(linked.Token)).ToArray();
        try
        {
            await Task.WhenAny(tasks).ConfigureAwait(false);

            // A faulted half fails the whole service — surface it now
            // instead of waiting on the survivors.
            foreach (Task task in tasks)
                if (task.IsFaulted)
                    await task.ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                Task? ended = tasks.FirstOrDefault(t => t.IsCompleted);
                string name = ended is null
                    ? halves[0].Name
                    : halves[Array.IndexOf(tasks, ended)].Name;
                throw new InvalidOperationException(
                    $"The {name} stopped unexpectedly; shutting the service down " +
                    "so the failure is visible instead of running half a service.");
            }
        }
        finally
        {
            linked.Cancel();
            // Give the surviving halves a bounded chance to observe the
            // cancellation; never let shutdown hang on them, and observe
            // any late fault so it can't surface as unobserved.
            Task all = Task.WhenAll(tasks);
            _ = all.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)))
                .ConfigureAwait(false);
        }
    }
}
