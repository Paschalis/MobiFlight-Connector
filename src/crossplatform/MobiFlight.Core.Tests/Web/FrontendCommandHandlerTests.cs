using System.Text.Json.Nodes;
using MobiFlight.Core.Project;
using MobiFlight.Core.Web;

namespace MobiFlight.Core.Tests.Web;

/// <summary>
/// The editing loop: a command arrives from the browser, the project changes, and the updated
/// project goes back out.
/// </summary>
[TestClass]
public sealed class FrontendCommandHandlerTests
{
    private FrontendHost _host = null!;
    private FrontendCommandHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        // Never started, so nothing binds a port: broadcasts to zero clients are a no-op and the
        // command handling itself is what is under test.
        _host = new FrontendHost(0, webRoot: null);
        _handler = new FrontendCommandHandler(_host, new FrontendStateBroadcaster(_host))
        {
            Project = MfProject.CreateEmpty("Test"),
        };
    }

    [TestCleanup]
    public void Cleanup() => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Builds the message the browser would send. Composing the JSON rather than interpolating it
    /// keeps GUIDs and Windows paths from needing escaping.
    /// </summary>
    private Task Send(string key, JsonObject payload) =>
        _handler.HandleAsync(new JsonObject { ["key"] = key, ["payload"] = payload }.ToJsonString());

    private static JsonObject Ref(string guid) => new() { ["GUID"] = guid };

    private JsonArray Items => _handler.Project!.ConfigItems(0);

    private string AddItem(string name, string type = "OutputConfigItem") =>
        _handler.Project!.AddConfigItem(name, type)["GUID"]!.GetValue<string>();

    [TestMethod]
    public async Task AddsConfigItem()
    {
        await Send("CommandAddConfigItem", new JsonObject
        {
            ["name"] = "Gear Lamp",
            ["type"] = "OutputConfigItem",
        });

        Assert.AreEqual(1, Items.Count);
        Assert.AreEqual("Gear Lamp", Items[0]!["Name"]!.GetValue<string>());
        Assert.AreEqual("OutputConfigItem", Items[0]!["Type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UpdatesConfigItem()
    {
        var guid = AddItem("Original");

        await Send("CommandUpdateConfigItem", new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["GUID"] = guid,
                ["Name"] = "Renamed",
                ["Active"] = false,
                ["Type"] = "OutputConfigItem",
            },
        });

        Assert.AreEqual("Renamed", Items[0]!["Name"]!.GetValue<string>());
        Assert.IsFalse(Items[0]!["Active"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task DeletesViaContextMenu()
    {
        var guid = AddItem("Doomed");

        await Send("CommandConfigContextMenu", new JsonObject
        {
            ["action"] = "delete",
            ["item"] = Ref(guid),
        });

        Assert.AreEqual(0, Items.Count);
    }

    [TestMethod]
    public async Task DuplicatesViaContextMenu()
    {
        var guid = AddItem("Original");

        await Send("CommandConfigContextMenu", new JsonObject
        {
            ["action"] = "duplicate",
            ["item"] = Ref(guid),
        });

        Assert.AreEqual(2, Items.Count);
        Assert.AreNotEqual(guid, Items[1]!["GUID"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task TogglesViaContextMenu()
    {
        var guid = AddItem("Switch");

        await Send("CommandConfigContextMenu", new JsonObject
        {
            ["action"] = "toggle",
            ["item"] = Ref(guid),
        });

        Assert.IsFalse(Items[0]!["Active"]!.GetValue<bool>(), "new items start active");
    }

    [TestMethod]
    public async Task EditActionDoesNotMutate()
    {
        var guid = AddItem("Thing");

        // "edit" only opens a dialog in the UI; the change arrives later as an update.
        await Send("CommandConfigContextMenu", new JsonObject
        {
            ["action"] = "edit",
            ["item"] = Ref(guid),
        });

        Assert.AreEqual(1, Items.Count);
        Assert.AreEqual("Thing", Items[0]!["Name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task BulkDeletes()
    {
        var a = AddItem("A");
        var b = AddItem("B");
        AddItem("C");

        await Send("CommandConfigBulkAction", new JsonObject
        {
            ["action"] = "delete",
            ["items"] = new JsonArray(Ref(a), Ref(b)),
        });

        Assert.AreEqual(1, Items.Count);
        Assert.AreEqual("C", Items[0]!["Name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ResortsItems()
    {
        AddItem("First");
        var second = AddItem("Second");

        await Send("CommandResortConfigItem", new JsonObject
        {
            ["items"] = new JsonArray(Ref(second)),
            ["newIndex"] = 0,
        });

        Assert.AreEqual("Second", Items[0]!["Name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RenamesProjectFromToolbar()
    {
        await Send("CommandProjectToolbar", new JsonObject
        {
            ["action"] = "rename",
            ["value"] = "My Cockpit",
        });

        Assert.AreEqual("My Cockpit", _handler.Project!.Name);
    }

    [TestMethod]
    public async Task RaisesRunAndStop()
    {
        var requests = new List<string>();
        _handler.RunRequested += (_, action) => requests.Add(action);

        await Send("CommandProjectToolbar", new JsonObject { ["action"] = "run" });
        await Send("CommandProjectToolbar", new JsonObject { ["action"] = "stop" });

        CollectionAssert.AreEqual(new[] { "run", "stop" }, requests);
    }

    [TestMethod]
    public async Task CreatesNewProject()
    {
        AddItem("Leftover");

        await Send("CommandMainMenu", new JsonObject { ["action"] = "file.new" });

        Assert.AreEqual(0, Items.Count, "a new project starts empty");
    }

    [TestMethod]
    public async Task SavesToDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-save-{Guid.NewGuid():N}.mfproj");
        AddItem("Persisted");

        try
        {
            await Send("CommandMainMenu", new JsonObject
            {
                ["action"] = "file.saveas",
                ["options"] = new JsonObject { ["filePath"] = path },
            });

            Assert.IsTrue(File.Exists(path), "the project was not written");

            var reloaded = MfProject.Load(path);
            Assert.AreEqual("Persisted", reloaded.ConfigItems(0)[0]!["Name"]!.GetValue<string>());
            Assert.IsFalse(_handler.Project!.HasUnsavedChanges);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task OpensAProjectByPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-open-{Guid.NewGuid():N}.mfproj");
        var source = MfProject.CreateEmpty("Opened");
        source.AddConfigItem("FromDisk", "OutputConfigItem");
        source.Save(path);

        try
        {
            await Send("CommandMainMenu", new JsonObject
            {
                ["action"] = "file.open",
                ["options"] = new JsonObject { ["filePath"] = path },
            });

            Assert.AreEqual("Opened", _handler.Project!.Name);
            Assert.AreEqual("FromDisk", Items[0]!["Name"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TracksActiveConfigFile()
    {
        _handler.Project!.AddConfigFile("Second");

        await Send("CommandActiveConfigFile", new JsonObject { ["index"] = 1 });
        Assert.AreEqual(1, _handler.ActiveConfigFile);

        await Send("CommandAddConfigItem", new JsonObject
        {
            ["name"] = "Goes To Second",
            ["type"] = "OutputConfigItem",
        });

        Assert.AreEqual(0, _handler.Project!.ConfigItems(0).Count);
        Assert.AreEqual(1, _handler.Project!.ConfigItems(1).Count,
            "new items belong to the file the UI is showing");
    }

    [TestMethod]
    public async Task AddsAndRenamesConfigFiles()
    {
        await Send("CommandAddConfigFile", new JsonObject
        {
            ["type"] = "create",
            ["label"] = "Overhead",
        });
        Assert.AreEqual(2, _handler.Project!.ConfigFiles.Count);

        await Send("CommandFileContextMenu", new JsonObject
        {
            ["action"] = "rename",
            ["index"] = 1,
            ["file"] = new JsonObject { ["Label"] = "Renamed" },
        });
        Assert.AreEqual("Renamed", _handler.Project!.ConfigFiles[1]!["Label"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MalformedMessagesAreIgnored()
    {
        // None of these may throw, or one bad frame would take the session down.
        await _handler.HandleAsync("not json at all");
        await _handler.HandleAsync("{}");
        await _handler.HandleAsync("""{"key":"CommandAddConfigItem"}""");
        await _handler.HandleAsync("""{"key":"CommandUpdateConfigItem","payload":{}}""");
        await _handler.HandleAsync("""{"key":"SomethingUnknown","payload":{"a":1}}""");

        Assert.AreEqual(0, Items.Count);
    }

    [TestMethod]
    public async Task CommandsWithoutAProjectAreSafe()
    {
        _handler.Project = null;

        await Send("CommandAddConfigItem", new JsonObject { ["name"] = "x", ["type"] = "OutputConfigItem" });
        await Send("CommandConfigContextMenu", new JsonObject { ["action"] = "delete", ["item"] = Ref("x") });
        await Send("CommandMainMenu", new JsonObject { ["action"] = "file.save" });

        Assert.IsNull(_handler.Project);
    }
}
