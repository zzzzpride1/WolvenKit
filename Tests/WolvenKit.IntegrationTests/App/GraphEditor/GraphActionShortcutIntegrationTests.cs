using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using WolvenKit.App.Services;
using WolvenKit.Views.GraphEditor;
using Xunit;

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class GraphActionShortcutIntegrationTests
{
    [StaFact]
    public void SettingsManagerCopiesConfiguredBindingsInBothDirections()
    {
        var dto = JsonSerializer.Deserialize<SettingsDto>(
            "{\"SettingsVersion\":4,\"GraphActionShortcuts\":{\"Ctrl+Shift+K\":\"node:questAudioNodeDefinition\"}}")!;
        // An unloaded manager does not save to the user's config.json.
        var manager = dto.ToSettingsManager();
        var saved = JsonSerializer.Deserialize<SettingsDto>(JsonSerializer.Serialize(new SettingsDto(manager)))!;

        Assert.Single(manager.GraphActionShortcuts);
        Assert.Single(saved.GraphActionShortcuts);
        Assert.Equal("node:questAudioNodeDefinition", saved.GraphActionShortcuts["Ctrl+Shift+K"]);
    }

    [StaFact]
    public void PaletteSelectionAndShortcutExecutionUseTheSameAction()
    {
        var calls = 0;
        var item = new GraphActionPaletteItem
        {
            ActionId = "node:questAudioNodeDefinition",
            Title = "Audio",
            Category = "Common",
            Execute = () => calls++
        };
        var palette = new GraphActionPalette();
        palette.Open("All Actions for Quest Graph", [item]);
        var list = Assert.IsType<ListBox>(palette.FindName("ActionList"));
        var executedEvents = 0;
        palette.ActionExecuted += (_, _) => executedEvents++;

        typeof(GraphActionPalette).GetMethod("ExecuteSelectedItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(palette, [list]);
        Assert.Equal(1, calls);
        Assert.Equal(1, executedEvents);

        Assert.True(item.TryExecute());
        Assert.Equal(2, calls);
    }

    [StaFact]
    public void DisabledActionsCannotExecuteThroughEitherEntryPoint()
    {
        var calls = 0;
        var item = new GraphActionPaletteItem { Title = "Disabled", IsEnabled = false, Execute = () => calls++ };
        var palette = new GraphActionPalette();
        palette.Open("All Actions for Quest Graph", [item]);
        var list = Assert.IsType<ListBox>(palette.FindName("ActionList"));
        list.SelectedItem = item;

        typeof(GraphActionPalette).GetMethod("ExecuteSelectedItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(palette, [list]);

        Assert.False(item.TryExecute());
        Assert.Equal(0, calls);
    }
}
