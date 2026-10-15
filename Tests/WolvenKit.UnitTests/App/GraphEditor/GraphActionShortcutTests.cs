using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.RED4.Types;

namespace WolvenKit.UnitTests.App.GraphEditor;

[TestClass]
public class GraphActionShortcutTests
{
    [TestMethod]
    public void DefaultBindingsAreIncludedInPersistedSettings()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new SettingsDto()));

        Assert.IsTrue(json.RootElement.TryGetProperty("GraphActionShortcuts", out var bindings));
        Assert.AreEqual("node:questPauseConditionNodeDefinition", bindings.GetProperty("Ctrl+Alt+1").GetString());
        Assert.AreEqual("node:questFactsDBManagerNodeDefinition", bindings.GetProperty("Ctrl+Alt+2").GetString());
        Assert.AreEqual("node:questJournalNodeDefinition", bindings.GetProperty("Ctrl+Alt+3").GetString());
        Assert.AreEqual("node:questAudioNodeDefinition", bindings.GetProperty("Ctrl+Alt+4").GetString());
    }

    [TestMethod]
    public void GestureMatchingRequiresTheExactModifiers()
    {
        var bindings = GraphActionShortcuts.CreateDefaultBindings();

        Assert.AreEqual("node:questPauseConditionNodeDefinition",
            GraphActionShortcuts.GetActionId(bindings, Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, Key.D1, ModifierKeys.Control));
        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, Key.D1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift));
        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, Key.NumPad1, ModifierKeys.Control | ModifierKeys.Alt));
    }

    [TestMethod]
    public void ConfiguredTemplateGestureIgnoresMalformedAndEmptyEntries()
    {
        var bindings = new Dictionary<string, string>
        {
            ["Ctrl+NotAKey"] = "node:wrong",
            ["Ctrl+Shift+K"] = "template:questAudioNodeDefinition:user:Alert%20Phase",
            ["Ctrl+Alt+1"] = ""
        };

        Assert.AreEqual("template:questAudioNodeDefinition:user:Alert%20Phase",
            GraphActionShortcuts.GetActionId(bindings, Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.IsNull(GraphActionShortcuts.GetActionId(null, Key.K, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [TestMethod]
    public void DuplicateCanonicalGesturesAreIgnored()
    {
        var bindings = new Dictionary<string, string>
        {
            ["Ctrl+Alt+1"] = "node:first",
            ["Alt+Ctrl+1"] = "node:second"
        };

        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
    }

    [DataTestMethod]
    [DataRow(Key.S, ModifierKeys.Control)]
    [DataRow(Key.Z, ModifierKeys.Control)]
    [DataRow(Key.C, ModifierKeys.Control)]
    [DataRow(Key.V, ModifierKeys.Control)]
    [DataRow(Key.Y, ModifierKeys.Control)]
    [DataRow(Key.X, ModifierKeys.Control)]
    [DataRow(Key.A, ModifierKeys.Control)]
    [DataRow(Key.D, ModifierKeys.Control)]
    [DataRow(Key.N, ModifierKeys.Control)]
    [DataRow(Key.G, ModifierKeys.Control | ModifierKeys.Shift)]
    [DataRow(Key.B, ModifierKeys.Control)]
    [DataRow(Key.R, ModifierKeys.Control)]
    [DataRow(Key.S, ModifierKeys.Control | ModifierKeys.Alt)]
    [DataRow(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
    [DataRow(Key.Delete, ModifierKeys.None)]
    [DataRow(Key.Left, ModifierKeys.Control)]
    [DataRow(Key.Tab, ModifierKeys.Control)]
    [DataRow(Key.F4, ModifierKeys.Alt)]
    [DataRow(Key.K, ModifierKeys.Windows)]
    public void ReservedGesturesNeverResolve(Key key, ModifierKeys modifiers)
    {
        var gesture = new KeyGesture(key, modifiers);
        var gestureText = new KeyGestureConverter().ConvertToInvariantString(gesture)!;
        var bindings = new Dictionary<string, string> { [gestureText] = "node:questAudioNodeDefinition" };

        Assert.IsNull(GraphActionShortcuts.GetActionId(bindings, key, modifiers));
    }

    [TestMethod]
    public void TemplateIdsDistinguishSourcesAndDoNotDependOnFilePathsOrRawLabels()
    {
        var type = typeof(questAudioNodeDefinition);
        var user = new RedTypeTemplateSelectionOption("Alert Phase", type, "old.json", RedTypeTemplateSelectionOptionSource.User);
        var system = new RedTypeTemplateSelectionOption("Alert Phase", type, "system.json", RedTypeTemplateSelectionOptionSource.System);
        var raw = new RedTypeTemplateSelectionOption("No Template", type, "", RedTypeTemplateSelectionOptionSource.Raw);

        Assert.AreEqual("node:questAudioNodeDefinition", GraphActionShortcuts.GetNodeActionId(type));
        Assert.AreEqual("template:questAudioNodeDefinition:user:Alert%20Phase", GraphActionShortcuts.GetTemplateActionId(type, user));
        Assert.AreNotEqual(GraphActionShortcuts.GetTemplateActionId(type, user), GraphActionShortcuts.GetTemplateActionId(type, system));
        user.FilePath = "moved.json";
        Assert.AreEqual("template:questAudioNodeDefinition:user:Alert%20Phase", GraphActionShortcuts.GetTemplateActionId(type, user));
        raw.Name = "Localized label";
        Assert.AreEqual("template:questAudioNodeDefinition:raw", GraphActionShortcuts.GetTemplateActionId(type, raw));
    }

    [TestMethod]
    public void SettingsJsonRoundTripPreservesConfiguredBindings()
    {
        var dto = JsonSerializer.Deserialize<SettingsDto>(
            "{\"SettingsVersion\":4,\"GraphActionShortcuts\":{\"Ctrl+Shift+K\":\"node:questAudioNodeDefinition\"}}")!;
        var saved = JsonSerializer.Deserialize<SettingsDto>(JsonSerializer.Serialize(dto))!;

        Assert.AreEqual(1, saved.GraphActionShortcuts.Count);
        Assert.AreEqual("node:questAudioNodeDefinition", saved.GraphActionShortcuts["Ctrl+Shift+K"]);
    }

    [TestMethod]
    public void LegacySettingsReceiveDefaultsAndAnExplicitEmptyMapDisablesShortcuts()
    {
        var legacy = JsonSerializer.Deserialize<SettingsDto>("{\"SettingsVersion\":4}")!;
        var disabled = JsonSerializer.Deserialize<SettingsDto>("{\"SettingsVersion\":4,\"GraphActionShortcuts\":{}}")!;

        Assert.AreEqual("node:questPauseConditionNodeDefinition",
            GraphActionShortcuts.GetActionId(legacy.GraphActionShortcuts, Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.AreEqual(0, disabled.GraphActionShortcuts.Count);
        Assert.IsNull(GraphActionShortcuts.GetActionId(disabled.GraphActionShortcuts, Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
    }
}
