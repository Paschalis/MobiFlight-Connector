using System.Text.Json;
using System.Text.Json.Nodes;
using MobiFlight.Core.Project;

namespace MobiFlight.Core.Web;

/// <summary>
/// Applies the commands the frontend sends back, so a project can be edited from the browser.
/// </summary>
/// <remarks>
/// The frontend is optimistic: it mutates its own state, sends the command, and expects the backend
/// to become the source of truth. Every handler therefore ends by broadcasting the project again,
/// which also corrects the UI when an edit was rejected.
/// </remarks>
public sealed class FrontendCommandHandler
{
    private readonly FrontendHost _host;
    private readonly FrontendStateBroadcaster _state;

    public FrontendCommandHandler(FrontendHost host, FrontendStateBroadcaster state)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>The project currently open, if any.</summary>
    public MfProject? Project { get; set; }

    /// <summary>Index of the config file the UI is showing.</summary>
    public int ActiveConfigFile { get; private set; }

    /// <summary>Raised when the UI asks to start or stop execution.</summary>
    public event EventHandler<string>? RunRequested;

    /// <summary>Raised for diagnostics.</summary>
    public event EventHandler<string>? Log;

    /// <summary>
    /// Handles one raw message from the browser.
    /// </summary>
    public async Task HandleAsync(string rawMessage)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(rawMessage) as JsonObject;
        }
        catch (JsonException ex)
        {
            Log?.Invoke(this, $"Could not parse message from the UI: {ex.Message}");
            return;
        }

        var key = message?["key"]?.GetValue<string>();
        if (key is null) return;

        var payload = message!["payload"] as JsonObject;

        try
        {
            switch (key)
            {
                case "CommandAddConfigItem":
                    await AddConfigItemAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandUpdateConfigItem":
                    await UpdateConfigItemAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandConfigContextMenu":
                    await ContextMenuAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandConfigBulkAction":
                    await BulkActionAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandResortConfigItem":
                    await ResortAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandActiveConfigFile":
                    if (payload?["index"]?.GetValue<int>() is { } index) ActiveConfigFile = index;
                    return;

                case "CommandAddConfigFile":
                    if (payload is not null) await AddConfigFileAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandFileContextMenu":
                    await FileContextMenuAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandMainMenu":
                    await MainMenuAsync(payload).ConfigureAwait(false);
                    return;

                case "CommandProjectToolbar":
                    await ToolbarAsync(payload).ConfigureAwait(false);
                    return;

                default:
                    // Plenty of commands (HubHop, authentication, presets) have no meaning here.
                    Log?.Invoke(this, $"Ignoring unsupported command '{key}'.");
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log?.Invoke(this, $"Command '{key}' failed: {ex.Message}");
            await _state.SendNotificationAsync("CommandFailed", ex.Message).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------- items

    private async Task AddConfigItemAsync(JsonObject? payload)
    {
        if (Project is null) return;

        // A command with no payload is malformed. Falling back to defaults here would let a
        // garbage frame add config items to the user's project.
        if (payload is null) return;

        var name = payload["name"]?.GetValue<string>() ?? "New Config";
        var type = payload["type"]?.GetValue<string>() ?? "OutputConfigItem";

        Project.AddConfigItem(name, type, ActiveConfigFile);

        await PublishProjectAsync().ConfigureAwait(false);
    }

    private async Task UpdateConfigItemAsync(JsonObject? payload)
    {
        if (Project is null) return;
        if (payload?["item"] is not JsonObject item) return;

        if (!Project.UpdateConfigItem(item))
        {
            Log?.Invoke(this, "Update ignored: no config item with that GUID.");
        }

        await PublishProjectAsync().ConfigureAwait(false);
    }

    private async Task ContextMenuAsync(JsonObject? payload)
    {
        if (Project is null) return;

        var action = payload?["action"]?.GetValue<string>();
        var guid = payload?["item"]?["GUID"]?.GetValue<string>();

        if (action is null || guid is null) return;

        switch (action)
        {
            case "delete":
                Project.RemoveConfigItem(guid);
                break;

            case "duplicate":
                Project.DuplicateConfigItem(guid);
                break;

            case "toggle":
                Project.ToggleConfigItem(guid);
                break;

            case "edit":
            case "settings":
                // The UI opens its own dialog and sends CommandUpdateConfigItem when done.
                return;

            case "test":
                // Driving an output without the sim is not implemented in the portable build.
                await _state.SendNotificationAsync("TestNotSupported",
                    "Test mode is not available in the cross platform build yet.").ConfigureAwait(false);
                return;
        }

        await PublishProjectAsync().ConfigureAwait(false);
    }

    private async Task BulkActionAsync(JsonObject? payload)
    {
        if (Project is null) return;

        var action = payload?["action"]?.GetValue<string>();
        if (payload?["items"] is not JsonArray items) return;

        foreach (var guid in items.Select(i => i?["GUID"]?.GetValue<string>()).Where(g => g is not null))
        {
            switch (action)
            {
                case "delete":
                    Project.RemoveConfigItem(guid!);
                    break;

                case "toggle":
                    Project.ToggleConfigItem(guid!);
                    break;
            }
        }

        await PublishProjectAsync().ConfigureAwait(false);
    }

    private async Task ResortAsync(JsonObject? payload)
    {
        if (Project is null) return;
        if (payload?["items"] is not JsonArray items) return;

        var guids = items.Select(i => i?["GUID"]?.GetValue<string>())
                         .Where(g => g is not null)
                         .Select(g => g!)
                         .ToList();

        var newIndex = payload["newIndex"]?.GetValue<int>() ?? 0;
        var fileIndex = payload["targetFileIndex"]?.GetValue<int>() ?? ActiveConfigFile;

        Project.ResortConfigItems(guids, newIndex, fileIndex);

        await PublishProjectAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------- files

    private async Task AddConfigFileAsync(JsonObject? payload)
    {
        if (Project is null) return;

        var label = payload?["label"]?.GetValue<string>() ?? $"Config {Project.ConfigFiles.Count + 1}";
        Project.AddConfigFile(label);

        await PublishProjectAsync().ConfigureAwait(false);
    }

    private async Task FileContextMenuAsync(JsonObject? payload)
    {
        if (Project is null) return;

        var action = payload?["action"]?.GetValue<string>();
        var index = payload?["index"]?.GetValue<int>() ?? -1;

        switch (action)
        {
            case "rename":
                var label = payload?["file"]?["Label"]?.GetValue<string>();
                if (label is not null) Project.RenameConfigFile(index, label);
                break;

            case "remove":
                if (!Project.RemoveConfigFile(index))
                {
                    await _state.SendNotificationAsync("CannotRemoveLastFile",
                        "A project needs at least one config file.").ConfigureAwait(false);
                    return;
                }
                break;

            case "export":
                await _state.SendNotificationAsync("ExportNotSupported",
                    "Exporting a config file is not available in the cross platform build yet.").ConfigureAwait(false);
                return;
        }

        await PublishProjectAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- menu

    private async Task MainMenuAsync(JsonObject? payload)
    {
        var action = payload?["action"]?.GetValue<string>();

        switch (action)
        {
            case "file.new":
                Project = MfProject.CreateEmpty();
                ActiveConfigFile = 0;
                await PublishProjectAsync().ConfigureAwait(false);
                return;

            case "file.open":
            case "file.recent":
            {
                // A browser cannot show a native file picker for the backend, so the path has to
                // come from the message.
                var path = payload?["options"]?["filePath"]?.GetValue<string>()
                        ?? payload?["options"]?["project"]?["FilePath"]?.GetValue<string>();

                if (path is null)
                {
                    await _state.SendNotificationAsync("OpenNeedsPath",
                        "Start the server with a project path; the browser cannot browse the server's disk.")
                        .ConfigureAwait(false);
                    return;
                }

                Project = MfProject.Load(path);
                ActiveConfigFile = 0;
                await PublishProjectAsync().ConfigureAwait(false);
                return;
            }

            case "file.save":
                await SaveAsync(null).ConfigureAwait(false);
                return;

            case "file.saveas":
                await SaveAsync(payload?["options"]?["filePath"]?.GetValue<string>()).ConfigureAwait(false);
                return;

            default:
                Log?.Invoke(this, $"Menu action '{action}' is not handled.");
                return;
        }
    }

    private async Task SaveAsync(string? path)
    {
        if (Project is null) return;

        if (path is null && Project.FilePath is null)
        {
            await _state.SendNotificationAsync("SaveNeedsPath",
                "This project has no file yet. Start the server with a path, or use Save As with one.")
                .ConfigureAwait(false);
            return;
        }

        await _host.BroadcastAsync("ProjectStatus",
            new FrontendMessages.ProjectStatus(true, "saving")).ConfigureAwait(false);

        Project.Save(path);

        await _host.BroadcastAsync("ProjectStatus",
            new FrontendMessages.ProjectStatus(false, "success")).ConfigureAwait(false);

        await _state.SendLogAsync($"Saved {Project.FilePath}").ConfigureAwait(false);
    }

    private async Task ToolbarAsync(JsonObject? payload)
    {
        var action = payload?["action"]?.GetValue<string>();

        switch (action)
        {
            case "run":
            case "stop":
                RunRequested?.Invoke(this, action);
                return;

            case "rename":
                if (Project is null) return;

                var name = payload?["value"]?.GetValue<string>();
                if (name is not null) Project.Name = name;

                await PublishProjectAsync().ConfigureAwait(false);
                return;

            case "test":
                await _state.SendNotificationAsync("TestNotSupported",
                    "Test mode is not available in the cross platform build yet.").ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Sends the project and its saved state to every browser.
    /// </summary>
    public async Task PublishProjectAsync()
    {
        if (Project is null) return;

        await _host.BroadcastAsync("Project", Project.ToFrontendProject()).ConfigureAwait(false);
        await _host.BroadcastAsync("ProjectStatus",
            new FrontendMessages.ProjectStatus(Project.HasUnsavedChanges)).ConfigureAwait(false);
    }
}
