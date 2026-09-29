using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claudette.Core.LoginItems;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Updates.Windows;

namespace Claudette.Platform.LoginItems.Windows;

/// <summary>
/// The MSIX's startup task, <c>windows.startupTask</c> in packaging/windows/Package.appxmanifest (DESIGN.md §9,
/// "Starting at login"), through <c>Windows.ApplicationModel.StartupTask</c>. Only a Claudette running from the package
/// can use it. Call it on one thread: the WinRT objects are polled between awaits, like the MSIX update's.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MsixStartupTask(TimeProvider timeProvider) : IPackageStartupTask
{
    /// <summary>The task's id in the manifest.</summary>
    public const string TaskId = "ClaudetteAtLogin";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public async Task<PackageTaskState> GetStateAsync(CancellationToken cancellationToken = default) =>
        (PackageTaskState)(await GetTaskAsync(cancellationToken)).GetState();

    public async Task<PackageTaskState> RequestEnableAsync(CancellationToken cancellationToken = default)
    {
        var task = await GetTaskAsync(cancellationToken);
        var operation = task.RequestEnableAsync();
        await WaitAsync(operation, cancellationToken);
        operation.Close();
        return (PackageTaskState)task.GetState();
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default) =>
        (await GetTaskAsync(cancellationToken)).Disable();

    private async Task<IStartupTask> GetTaskAsync(CancellationToken cancellationToken)
    {
        var statics = WinRt.GetActivationFactory<IStartupTaskStatics>("Windows.ApplicationModel.StartupTask");
        IAsyncInfo operation;
        using (var id = new HString(TaskId))
        {
            operation = statics.GetAsync(id.Handle);
        }
        await WaitAsync(operation, cancellationToken);
        var task = ((IStartupTaskOperation)operation).GetResults();
        operation.Close();
        return task;
    }

    /// <summary>Waits for a WinRT operation to finish, and throws its error if it failed.</summary>
    private async Task WaitAsync(IAsyncInfo operation, CancellationToken cancellationToken)
    {
        int status;
        while ((status = operation.GetStatus()) == Deployment.AsyncStarted)
        {
            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }
        if (status != Deployment.AsyncCompleted)
        {
            var error = status == Deployment.AsyncCanceled ? unchecked((int)0x800704C7) : operation.GetErrorCode();
            operation.Close();
            Marshal.ThrowExceptionForHR(error);
        }
    }
}
