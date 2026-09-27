namespace KeelMatrix.CliContract.Core;

internal static class CanonicalCommandContract
{
    internal static bool IsRunnable(CanonicalCommand command) =>
        string.Equals(command.Kind ?? "action", "action", StringComparison.Ordinal);

    internal static bool ContainsRunnableAction(CanonicalCommand command) =>
        IsRunnable(command) || command.Subcommands.Any(ContainsRunnableAction);
}
