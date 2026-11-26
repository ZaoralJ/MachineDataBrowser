using libplctag;

namespace MachineDataBrowser.Driver.Cip;

/// <summary>
/// Async reads and writes with a deadline of our own. libplctag.NET (2.0.0-alpha.5) drops the pending operation
/// when the core reports a <see cref="Status.Pending"/> completion, which libplctag 2.7 does while reconnecting; the
/// wrapper's timeout then finds nothing to cancel and the task never completes.
/// </summary>
internal static class TagOperations
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    public static Task ReadGuardedAsync(this Tag tag, CancellationToken cancellationToken) =>
        GuardAsync(tag, tag.ReadAsync(cancellationToken), cancellationToken);

    public static Task WriteGuardedAsync(this Tag tag, CancellationToken cancellationToken) =>
        GuardAsync(tag, tag.WriteAsync(cancellationToken), cancellationToken);

    private static async Task GuardAsync(Tag tag, Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(tag.Timeout + Grace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            tag.Abort();
            throw new LibPlcTagException(Status.ErrorTimeout);
        }
    }
}
