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
    public static async Task RunTogetherAsync(
        string firstName, Func<CancellationToken, Task> first,
        string secondName, Func<CancellationToken, Task> second,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task firstTask = first(linked.Token);
        Task secondTask = second(linked.Token);
        try
        {
            await Task.WhenAny(firstTask, secondTask).ConfigureAwait(false);

            // A faulted half fails the whole service — surface it now
            // instead of waiting on the survivor.
            if (firstTask.IsFaulted) await firstTask.ConfigureAwait(false);
            if (secondTask.IsFaulted) await secondTask.ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                string ended = firstTask.IsCompleted ? firstName : secondName;
                throw new InvalidOperationException(
                    $"The {ended} stopped unexpectedly; shutting the service down " +
                    "so the failure is visible instead of running half a service.");
            }
        }
        finally
        {
            linked.Cancel();
            // Give the surviving half a bounded chance to observe the
            // cancellation; never let shutdown hang on it, and observe
            // any late fault so it can't surface as unobserved.
            Task all = Task.WhenAll(firstTask, secondTask);
            _ = all.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)))
                .ConfigureAwait(false);
        }
    }
}
