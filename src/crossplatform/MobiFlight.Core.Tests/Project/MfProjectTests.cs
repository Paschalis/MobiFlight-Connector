using System.Text.Json.Nodes;
using MobiFlight.Core.Project;

namespace MobiFlight.Core.Tests.Project;

[TestClass]
public sealed class MfProjectTests
{
    /// <summary>
    /// Carries fields the portable build knows nothing about, which is the point: they have to
    /// survive an edit untouched.
    /// </summary>
    private const string ProjectJson = """
        {
          "_version": "2.0",
          "Name": "Test Project",
          "Sim": "xplane",
          "SomeFutureField": { "nested": [1, 2, 3] },
          "ConfigFiles": [
            {
              "Label": "Main",
              "FileName": null,
              "EmbedContent": true,
              "ConfigItems": [
                {
                  "GUID": "item-1",
                  "Active": true,
                  "Name": "Gear Light",
                  "Type": "OutputConfigItem",
                  "ModuleSerial": "Cockpit/ SN-1",
                  "Source": { "SourceType": "XPLANE", "XplaneDataRef": { "Path": "sim/test/a" }, "Type": "XplaneSource" },
                  "Device": { "Name": "Pin 13", "Type": "Output", "Pin": "13" },
                  "DeviceType": "Pin",
                  "Modifiers": { "Items": [ { "Type": "Transformation", "Expression": "$*2" } ] },
                  "Preconditions": [],
                  "ConfigRefs": []
                },
                {
                  "GUID": "item-2",
                  "Active": false,
                  "Name": "Beacon",
                  "Type": "OutputConfigItem"
                }
              ]
            }
          ]
        }
        """;

    private static MfProject Load(string json = ProjectJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-{Guid.NewGuid():N}.mfproj");
        File.WriteAllText(path, json);

        try
        {
            return MfProject.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ReadsNameAndItems()
    {
        var project = Load();

        Assert.AreEqual("Test Project", project.Name);
        Assert.AreEqual(1, project.ConfigFiles.Count);
        Assert.AreEqual(2, project.ConfigItems(0).Count);
        Assert.IsFalse(project.HasUnsavedChanges);
    }

    /// <summary>
    /// The most important behaviour here. A project holds far more than this build models, and an
    /// edit must not quietly discard the rest.
    /// </summary>
    [TestMethod]
    public void PreservesUnknownFieldsAcrossAnEditAndSave()
    {
        var project = Load();
        project.ToggleConfigItem("item-1");

        var path = Path.Combine(Path.GetTempPath(), $"mf-out-{Guid.NewGuid():N}.mfproj");
        try
        {
            project.Save(path);

            var reloaded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.AreEqual("2.0", reloaded["_version"]!.GetValue<string>(), "schema version was dropped");
            Assert.AreEqual(3, reloaded["SomeFutureField"]!["nested"]!.AsArray().Count,
                "an unmodelled field was dropped");

            var item = reloaded["ConfigFiles"]![0]!["ConfigItems"]![0]!;
            Assert.AreEqual("$*2", item["Modifiers"]!["Items"]![0]!["Expression"]!.GetValue<string>(),
                "modifiers are not modelled here and must round-trip untouched");
            Assert.AreEqual("sim/test/a", item["Source"]!["XplaneDataRef"]!["Path"]!.GetValue<string>());
            Assert.IsFalse(item["Active"]!.GetValue<bool>(), "the edit itself did not apply");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SaveDoesNotPersistFilePath()
    {
        var project = Load();
        var path = Path.Combine(Path.GetTempPath(), $"mf-out-{Guid.NewGuid():N}.mfproj");

        try
        {
            project.Save(path);
            var reloaded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.IsFalse(reloaded.ContainsKey("FilePath"), "FilePath is derived, not stored");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AddsConfigItemWithFreshGuid()
    {
        var project = Load();

        var added = project.AddConfigItem("New Switch", "InputConfigItem");

        Assert.AreEqual(3, project.ConfigItems(0).Count);
        Assert.AreEqual("New Switch", added["Name"]!.GetValue<string>());
        Assert.AreEqual("InputConfigItem", added["Type"]!.GetValue<string>());
        Assert.IsTrue(Guid.TryParse(added["GUID"]!.GetValue<string>(), out _), "a real GUID is expected");
        Assert.IsTrue(project.HasUnsavedChanges);
    }

    [TestMethod]
    public void UpdatesItemByGuid()
    {
        var project = Load();

        var updated = new JsonObject
        {
            ["GUID"] = "item-2",
            ["Active"] = true,
            ["Name"] = "Beacon Light",
            ["Type"] = "OutputConfigItem",
        };

        Assert.IsTrue(project.UpdateConfigItem(updated));

        var item = project.AllConfigItems().Single(i => i["GUID"]!.GetValue<string>() == "item-2");
        Assert.AreEqual("Beacon Light", item["Name"]!.GetValue<string>());
        Assert.IsTrue(item["Active"]!.GetValue<bool>());
    }

    [TestMethod]
    public void UpdateIsRejectedForUnknownGuid()
    {
        var project = Load();

        var orphan = new JsonObject { ["GUID"] = "does-not-exist", ["Name"] = "Ghost" };

        Assert.IsFalse(project.UpdateConfigItem(orphan));
        Assert.AreEqual(2, project.ConfigItems(0).Count, "a rejected update must not add anything");
    }

    [TestMethod]
    public void RemovesItem()
    {
        var project = Load();

        Assert.IsTrue(project.RemoveConfigItem("item-1"));
        Assert.AreEqual(1, project.ConfigItems(0).Count);
        Assert.IsFalse(project.RemoveConfigItem("item-1"), "removing twice is a no-op");
    }

    [TestMethod]
    public void DuplicateGetsItsOwnGuid()
    {
        var project = Load();

        var copy = project.DuplicateConfigItem("item-1");

        Assert.IsNotNull(copy);
        Assert.AreNotEqual("item-1", copy!["GUID"]!.GetValue<string>(), "a duplicate must not share the GUID");
        StringAssert.Contains(copy["Name"]!.GetValue<string>(), "copy");
        Assert.AreEqual(3, project.ConfigItems(0).Count);

        // Inserted right after the original rather than appended.
        Assert.AreEqual("item-1", project.ConfigItems(0)[0]!["GUID"]!.GetValue<string>());
        Assert.AreEqual(copy["GUID"]!.GetValue<string>(), project.ConfigItems(0)[1]!["GUID"]!.GetValue<string>());
    }

    [TestMethod]
    public void DuplicateKeepsTheRestOfTheItem()
    {
        var project = Load();
        var copy = project.DuplicateConfigItem("item-1")!;

        Assert.AreEqual("sim/test/a", copy["Source"]!["XplaneDataRef"]!["Path"]!.GetValue<string>());
        Assert.AreEqual("$*2", copy["Modifiers"]!["Items"]![0]!["Expression"]!.GetValue<string>());
    }

    [TestMethod]
    public void TogglesActive()
    {
        var project = Load();

        project.ToggleConfigItem("item-2");

        var item = project.AllConfigItems().Single(i => i["GUID"]!.GetValue<string>() == "item-2");
        Assert.IsTrue(item["Active"]!.GetValue<bool>(), "item-2 started inactive");
    }

    [TestMethod]
    public void ResortMovesItems()
    {
        var project = Load();

        project.ResortConfigItems(["item-2"], newIndex: 0);

        Assert.AreEqual("item-2", project.ConfigItems(0)[0]!["GUID"]!.GetValue<string>());
        Assert.AreEqual("item-1", project.ConfigItems(0)[1]!["GUID"]!.GetValue<string>());
    }

    [TestMethod]
    public void ResortClampsOutOfRangeIndex()
    {
        var project = Load();

        project.ResortConfigItems(["item-1"], newIndex: 99);

        Assert.AreEqual(2, project.ConfigItems(0).Count, "nothing may be lost");
        Assert.AreEqual("item-1", project.ConfigItems(0)[1]!["GUID"]!.GetValue<string>());
    }

    [TestMethod]
    public void ManagesConfigFiles()
    {
        var project = Load();

        project.AddConfigFile("Second");
        Assert.AreEqual(2, project.ConfigFiles.Count);

        Assert.IsTrue(project.RenameConfigFile(1, "Renamed"));
        Assert.AreEqual("Renamed", project.ConfigFiles[1]!["Label"]!.GetValue<string>());

        Assert.IsTrue(project.RemoveConfigFile(1));
        Assert.AreEqual(1, project.ConfigFiles.Count);

        Assert.IsFalse(project.RemoveConfigFile(0), "the last config file must not be removable");
    }

    [TestMethod]
    public void FrontendProjectFillsInRequiredFields()
    {
        var project = Load("""{ "Name": "Bare", "ConfigFiles": [ { "ConfigItems": [] } ] }""");

        var payload = project.ToFrontendProject();

        Assert.AreEqual("Bare", payload["Name"]!.GetValue<string>());
        Assert.IsNotNull(payload["Sim"], "the UI expects a Sim value");
        Assert.IsNotNull(payload["Features"]);
        Assert.IsNotNull(payload["ControllerBindings"]);
        Assert.IsNotNull(payload["ConfigFiles"]![0]!["Label"], "the UI labels each config file tab");
    }

    [TestMethod]
    public void FrontendProjectIsACopy()
    {
        var project = Load();

        var payload = project.ToFrontendProject();
        payload["Name"] = "Mutated";

        Assert.AreEqual("Test Project", project.Name, "the broadcast payload must not alias the document");
    }

    [TestMethod]
    public void RejectsLegacyXmlProject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-{Guid.NewGuid():N}.mcc");
        File.WriteAllText(path, "<?xml version=\"1.0\"?><MobiflightConnector></MobiflightConnector>");

        try
        {
            var error = Assert.ThrowsExactly<NotSupportedException>(() => MfProject.Load(path));
            StringAssert.Contains(error.Message, ".mfproj", "the message should say what to do instead");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void DetectsJsonRegardlessOfLeadingWhitespaceOrBom()
    {
        Assert.IsTrue(MfProject.LooksLikeJson("{}"));
        Assert.IsTrue(MfProject.LooksLikeJson("\n  { }"));
        Assert.IsTrue(MfProject.LooksLikeJson("﻿{}"), "a UTF-8 BOM must not fool the check");
        Assert.IsFalse(MfProject.LooksLikeJson("<?xml version=\"1.0\"?>"));
    }

    [TestMethod]
    public void EmptyProjectIsUsableImmediately()
    {
        var project = MfProject.CreateEmpty("Fresh");

        Assert.AreEqual("Fresh", project.Name);
        Assert.AreEqual(1, project.ConfigFiles.Count);

        project.AddConfigItem("First", "OutputConfigItem");
        Assert.AreEqual(1, project.ConfigItems(0).Count);
    }

    [TestMethod]
    public void SaveWithoutPathFails()
    {
        var project = MfProject.CreateEmpty();

        Assert.ThrowsExactly<InvalidOperationException>(() => project.Save());
    }

    [TestMethod]
    public void SaveClearsTheDirtyFlag()
    {
        var project = Load();
        project.AddConfigItem("x", "OutputConfigItem");
        Assert.IsTrue(project.HasUnsavedChanges);

        var path = Path.Combine(Path.GetTempPath(), $"mf-out-{Guid.NewGuid():N}.mfproj");
        try
        {
            project.Save(path);
            Assert.IsFalse(project.HasUnsavedChanges);
            Assert.AreEqual(path, project.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real project files shipped with the Connector must load and re-save without loss.
    /// </summary>
    [TestMethod]
    public void RoundTripsBundledProjects()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Assert.Inconclusive("Repository root not found relative to the test output directory.");
            return;
        }

        var files = Directory.GetFiles(repoRoot, "*.mfproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.IsNotEmpty(files, "expected at least one bundled .mfproj");

        foreach (var file in files)
        {
            var project = MfProject.Load(file);

            var output = Path.Combine(Path.GetTempPath(), $"mf-rt-{Guid.NewGuid():N}.mfproj");
            try
            {
                project.Save(output);

                var original = JsonNode.Parse(File.ReadAllText(file))!;
                var written = JsonNode.Parse(File.ReadAllText(output))!;

                // FilePath is intentionally not persisted; everything else must match.
                if (original is JsonObject originalObject) originalObject.Remove("FilePath");

                Assert.IsTrue(JsonNode.DeepEquals(original, written),
                    $"{Path.GetFileName(file)} changed when loaded and saved unmodified");
            }
            finally
            {
                File.Delete(output);
            }
        }
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "MobiFlightConnector")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
