using System.Text.Json;
using System.Text.Json.Nodes;

namespace MobiFlight.Core.Project;

/// <summary>
/// A MobiFlight project (.mfproj), held as a JSON document rather than a typed model.
/// </summary>
/// <remarks>
/// <para>
/// Working on the DOM is deliberate. A project carries far more per-item detail than the portable
/// build understands (modifiers, device sub-types, controller bindings, schema version). Mapping it
/// onto typed classes would silently drop everything unmodelled the first time a user saved, which
/// is the worst failure this code could have. Keeping the original nodes means an edit changes only
/// what it touches and everything else round-trips untouched.
/// </para>
/// <para>
/// It also happens to be what the frontend wants: the .mfproj item shape and the frontend's
/// IConfigItem are the same thing, so items can be handed over verbatim.
/// </para>
/// </remarks>
public sealed class MfProject
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly JsonObject _root;

    private MfProject(JsonObject root, string? path)
    {
        _root = root;
        FilePath = path;
    }

    /// <summary>Where the project was loaded from, or will be saved to.</summary>
    public string? FilePath { get; set; }

    /// <summary>True when there are edits that have not been written to disk.</summary>
    public bool HasUnsavedChanges { get; private set; }

    public string Name
    {
        get => _root["Name"]?.GetValue<string>() ?? "Untitled";
        set
        {
            _root["Name"] = value;
            HasUnsavedChanges = true;
        }
    }

    /// <summary>
    /// Loads a project. Only the JSON format (.mfproj) is supported for editing.
    /// </summary>
    public static MfProject Load(string path)
    {
        var text = File.ReadAllText(path);

        if (!LooksLikeJson(text))
        {
            throw new NotSupportedException(
                $"'{Path.GetFileName(path)}' is a legacy XML project. Open it once in the Windows " +
                "Connector and save it as .mfproj, or use 'mobiflight run' which reads .mcc directly.");
        }

        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("The project file is not a JSON object.");

        return new MfProject(root, path);
    }

    /// <summary>
    /// Creates an empty project with a single config file.
    /// </summary>
    public static MfProject CreateEmpty(string name = "New Project")
    {
        var root = new JsonObject
        {
            ["Name"] = name,
            ["ConfigFiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["Label"] = "Config",
                    ["FileName"] = null,
                    ["EmbedContent"] = true,
                    ["ReferenceOnly"] = false,
                    ["ConfigItems"] = new JsonArray(),
                },
            },
        };

        return new MfProject(root, null);
    }

    internal static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>The config files, created on demand if the project has none.</summary>
    public JsonArray ConfigFiles
    {
        get
        {
            if (_root["ConfigFiles"] is JsonArray existing) return existing;

            var created = new JsonArray();
            _root["ConfigFiles"] = created;
            return created;
        }
    }

    /// <summary>The config items of one file.</summary>
    public JsonArray ConfigItems(int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= ConfigFiles.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex), $"No config file at index {fileIndex}.");
        }

        var file = ConfigFiles[fileIndex]!.AsObject();

        if (file["ConfigItems"] is JsonArray items) return items;

        var created = new JsonArray();
        file["ConfigItems"] = created;
        return created;
    }

    /// <summary>Every config item across every file.</summary>
    public IEnumerable<JsonObject> AllConfigItems()
    {
        for (var i = 0; i < ConfigFiles.Count; i++)
        {
            foreach (var item in ConfigItems(i))
            {
                if (item is JsonObject obj) yield return obj;
            }
        }
    }

    /// <summary>
    /// Adds a new, empty config item and returns it.
    /// </summary>
    public JsonObject AddConfigItem(string name, string type, int fileIndex = 0)
    {
        var item = new JsonObject
        {
            ["GUID"] = Guid.NewGuid().ToString(),
            ["Active"] = true,
            ["Name"] = name,
            ["Type"] = type,
            ["Preconditions"] = new JsonArray(),
            ["ConfigRefs"] = new JsonArray(),
            ["Modifiers"] = new JsonObject { ["Items"] = new JsonArray() },
        };

        ConfigItems(fileIndex).Add(item);
        HasUnsavedChanges = true;

        return item;
    }

    /// <summary>
    /// Replaces an item, matched by GUID.
    /// </summary>
    /// <returns>false when no item with that GUID exists.</returns>
    public bool UpdateConfigItem(JsonObject updated)
    {
        var guid = updated["GUID"]?.GetValue<string>();
        if (string.IsNullOrEmpty(guid)) return false;

        for (var fileIndex = 0; fileIndex < ConfigFiles.Count; fileIndex++)
        {
            var items = ConfigItems(fileIndex);

            for (var i = 0; i < items.Count; i++)
            {
                if (GuidOf(items[i]) != guid) continue;

                // Deep clone so the incoming node is not parented into this document twice.
                items[i] = updated.DeepClone();
                HasUnsavedChanges = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes an item by GUID.</summary>
    public bool RemoveConfigItem(string guid)
    {
        for (var fileIndex = 0; fileIndex < ConfigFiles.Count; fileIndex++)
        {
            var items = ConfigItems(fileIndex);

            for (var i = 0; i < items.Count; i++)
            {
                if (GuidOf(items[i]) != guid) continue;

                items.RemoveAt(i);
                HasUnsavedChanges = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies an item, giving the copy a fresh GUID so the two never collide.
    /// </summary>
    public JsonObject? DuplicateConfigItem(string guid)
    {
        for (var fileIndex = 0; fileIndex < ConfigFiles.Count; fileIndex++)
        {
            var items = ConfigItems(fileIndex);

            for (var i = 0; i < items.Count; i++)
            {
                if (GuidOf(items[i]) != guid) continue;

                var copy = items[i]!.DeepClone().AsObject();
                copy["GUID"] = Guid.NewGuid().ToString();
                copy["Name"] = (copy["Name"]?.GetValue<string>() ?? "Config") + " (copy)";

                items.Insert(i + 1, copy);
                HasUnsavedChanges = true;
                return copy;
            }
        }

        return null;
    }

    /// <summary>Flips an item's Active flag.</summary>
    public bool ToggleConfigItem(string guid)
    {
        foreach (var item in AllConfigItems())
        {
            if (GuidOf(item) != guid) continue;

            var active = item["Active"]?.GetValue<bool>() ?? true;
            item["Active"] = !active;
            HasUnsavedChanges = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Moves items to a new position, used by drag and drop in the UI.
    /// </summary>
    public void ResortConfigItems(IReadOnlyList<string> guids, int newIndex, int fileIndex = 0)
    {
        var items = ConfigItems(fileIndex);

        // Pull the moved items out first, preserving their relative order.
        var moving = new List<JsonNode>();
        foreach (var guid in guids)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (GuidOf(items[i]) != guid) continue;

                var node = items[i]!.DeepClone();
                items.RemoveAt(i);
                moving.Add(node);
                break;
            }
        }

        if (moving.Count == 0) return;

        var target = Math.Clamp(newIndex, 0, items.Count);

        for (var i = 0; i < moving.Count; i++)
        {
            items.Insert(target + i, moving[i]);
        }

        HasUnsavedChanges = true;
    }

    /// <summary>Adds an empty config file.</summary>
    public void AddConfigFile(string label)
    {
        ConfigFiles.Add(new JsonObject
        {
            ["Label"] = label,
            ["FileName"] = null,
            ["EmbedContent"] = true,
            ["ReferenceOnly"] = false,
            ["ConfigItems"] = new JsonArray(),
        });

        HasUnsavedChanges = true;
    }

    public bool RenameConfigFile(int index, string label)
    {
        if (index < 0 || index >= ConfigFiles.Count) return false;

        ConfigFiles[index]!.AsObject()["Label"] = label;
        HasUnsavedChanges = true;
        return true;
    }

    public bool RemoveConfigFile(int index)
    {
        // A project always needs somewhere to put items.
        if (index < 0 || index >= ConfigFiles.Count || ConfigFiles.Count <= 1) return false;

        ConfigFiles.RemoveAt(index);
        HasUnsavedChanges = true;
        return true;
    }

    /// <summary>
    /// The payload for the frontend's "Project" message.
    /// </summary>
    /// <remarks>
    /// The stored shape already matches what the UI expects, so this fills in only the fields the
    /// frontend requires but the file does not carry, and hands the rest over untouched.
    /// </remarks>
    public JsonObject ToFrontendProject()
    {
        var project = _root.DeepClone().AsObject();

        project["FilePath"] = FilePath ?? string.Empty;
        project["Sim"] ??= "xplane";
        project["ControllerBindings"] ??= new JsonArray();
        project["Features"] ??= new JsonObject { ["FSUIPC"] = false, ["ProSim"] = false };

        // The frontend reads ConfigFiles[].Label, which older files may not carry.
        for (var i = 0; i < ConfigFiles.Count; i++)
        {
            var file = project["ConfigFiles"]![i]!.AsObject();
            file["Label"] ??= $"Config {i + 1}";
            file["ConfigItems"] ??= new JsonArray();
        }

        return project;
    }

    /// <summary>
    /// Writes the project back to disk.
    /// </summary>
    public void Save(string? path = null)
    {
        var target = path ?? FilePath
            ?? throw new InvalidOperationException("The project has no file path; use Save(path).");

        // FilePath is derived from where the file lives, so it is never persisted.
        var document = _root.DeepClone().AsObject();
        document.Remove("FilePath");

        File.WriteAllText(target, document.ToJsonString(WriteOptions));

        FilePath = target;
        HasUnsavedChanges = false;
    }

    private static string? GuidOf(JsonNode? node) => node?["GUID"]?.GetValue<string>();
}
