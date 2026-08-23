namespace MobiFlight.Core.Web;

/// <summary>
/// Payloads the frontend understands.
/// </summary>
/// <remarks>
/// Member names deliberately match the TypeScript interfaces in
/// <c>src/MobiFlightConnector/frontend/src/types/messages.d.ts</c> exactly, including their casing.
/// The frontend reads them by name, so a mismatch produces a silently blank UI rather than an error.
/// </remarks>
public static class FrontendMessages
{
    /// <summary>Text and progress shown in the status bar.</summary>
    public sealed record StatusBarUpdate(string Text, int Value);

    /// <summary>Drives the run and test buttons.</summary>
    public sealed record ExecutionState(bool IsRunning, bool IsTesting, bool RunAvailable, bool TestAvailable);

    /// <summary>Whether the project has unsaved changes.</summary>
    public sealed record ProjectStatus(bool HasChanged, string SaveStatus = "idle");

    /// <summary>Shows or hides the blocking overlay.</summary>
    public sealed record OverlayState(bool Visible);

    /// <summary>The controllers currently connected.</summary>
    public sealed record ConnectedControllers(IReadOnlyList<Controller> Controllers);

    /// <summary>One connected controller.</summary>
    public sealed record Controller(string Name, string Serial, string Type, string Port);

    /// <summary>A line for the log panel.</summary>
    public sealed record LogEntry(string Severity, string Message, string Timestamp);

    /// <summary>Current values of the MobiFlight variables.</summary>
    public sealed record MobiFlightVariablesUpdate(IReadOnlyList<VariableValue> Variables);

    public sealed record VariableValue(string Name, double Number, string? Text);

    /// <summary>A transient toast in the UI.</summary>
    public sealed record Notification(string Event, string? Message = null);
}

/// <summary>
/// Publishes the state the frontend expects, so a browser connecting to the portable build sees a
/// populated UI rather than an empty shell.
/// </summary>
public sealed class FrontendStateBroadcaster(FrontendHost host)
{
    private readonly FrontendHost _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>
    /// Sends the initial burst a freshly connected browser needs.
    /// </summary>
    public async Task SendInitialStateAsync(
        string statusText,
        IReadOnlyList<FrontendMessages.Controller> controllers,
        bool running)
    {
        await _host.BroadcastAsync("OverlayState", new FrontendMessages.OverlayState(false)).ConfigureAwait(false);
        await _host.BroadcastAsync("ProjectStatus", new FrontendMessages.ProjectStatus(false)).ConfigureAwait(false);
        await SendExecutionStateAsync(running).ConfigureAwait(false);
        await SendControllersAsync(controllers).ConfigureAwait(false);
        await SendStatusAsync(statusText).ConfigureAwait(false);
    }

    public Task SendStatusAsync(string text, int value = 0) =>
        _host.BroadcastAsync("StatusBarUpdate", new FrontendMessages.StatusBarUpdate(text, value));

    public Task SendExecutionStateAsync(bool running) =>
        _host.BroadcastAsync("ExecutionState", new FrontendMessages.ExecutionState(
            IsRunning: running,
            IsTesting: false,
            RunAvailable: !running,
            // Test mode drives outputs without a sim; not implemented in the portable build yet.
            TestAvailable: false));

    public Task SendControllersAsync(IReadOnlyList<FrontendMessages.Controller> controllers) =>
        _host.BroadcastAsync("ConnectedControllers", new FrontendMessages.ConnectedControllers(controllers));

    public Task SendLogAsync(string message, string severity = "Info") =>
        _host.BroadcastAsync("LogEntry", new FrontendMessages.LogEntry(
            severity, message, DateTime.Now.ToString("HH:mm:ss")));

    public Task SendVariablesAsync(IEnumerable<Project.MobiFlightVariable> variables) =>
        _host.BroadcastAsync("MobiFlightVariablesUpdate", new FrontendMessages.MobiFlightVariablesUpdate(
            variables.Select(v => new FrontendMessages.VariableValue(v.Name, v.Number, v.Text)).ToList()));

    public Task SendNotificationAsync(string @event, string? message = null) =>
        _host.BroadcastAsync("Notification", new FrontendMessages.Notification(@event, message));
}
