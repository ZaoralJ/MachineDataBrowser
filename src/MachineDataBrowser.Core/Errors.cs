namespace MachineDataBrowser.Core;

public static class Errors
{
    /// <summary>
    /// Exceptions a client can report and continue after. Anything else (out of memory, stack overflow, access
    /// violation) leaves the process in an unknown state and must not be swallowed.
    /// </summary>
    public static bool IsRecoverable(Exception ex) =>
        ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or InsufficientExecutionStackException);
}
