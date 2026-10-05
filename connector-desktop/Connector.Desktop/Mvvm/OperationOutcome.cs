namespace Connector.Desktop.Mvvm;

// Typed outcome for a native command. Consumers use this instead of inferring success from a completed Task.
public enum OperationOutcome
{
    None,
    Running,
    Succeeded,
    Rejected,
    Cancelled,
    Failed
}
