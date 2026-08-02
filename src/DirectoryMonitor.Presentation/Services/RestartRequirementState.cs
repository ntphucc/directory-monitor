using DirectoryMonitor.Application;

namespace DirectoryMonitor.Presentation.Services;

public sealed class RestartRequirementState : IRestartRequirementState
{
    public bool IsDatabaseRestartRequired { get; private set; }
    public string? DatabaseRestartMessage { get; private set; }
    public event EventHandler? Changed;

    public void RequireDatabaseRestart(string message)
    {
        IsDatabaseRestartRequired = true;
        DatabaseRestartMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
