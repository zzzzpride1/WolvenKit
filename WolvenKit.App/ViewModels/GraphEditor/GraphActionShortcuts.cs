using System;
using System.Collections.Generic;
using System.Windows.Input;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

/// <summary>
/// Gesture mappings identify palette actions; node creation stays in the palette.
/// </summary>
public static class GraphActionShortcuts
{
    public static Dictionary<string, string> CreateDefaultBindings() => new()
    {
        ["Ctrl+Alt+1"] = GetNodeActionId(typeof(questPauseConditionNodeDefinition)),
        ["Ctrl+Alt+2"] = GetNodeActionId(typeof(questFactsDBManagerNodeDefinition)),
        ["Ctrl+Alt+3"] = GetNodeActionId(typeof(questJournalNodeDefinition)),
        ["Ctrl+Alt+4"] = GetNodeActionId(typeof(questAudioNodeDefinition))
    };

    // The palette catalog already identifies RED node types by Type.Name.
    public static string GetNodeActionId(Type type) => $"node:{type.Name}";

    public static string GetTemplateActionId(Type type, RedTypeTemplateSelectionOption template) =>
        template.Source == RedTypeTemplateSelectionOptionSource.Raw
            ? $"template:{type.Name}:raw"
            : $"template:{type.Name}:{template.Source.ToString().ToLowerInvariant()}:{Uri.EscapeDataString(template.Name)}";

    /// <summary>
    /// Invalid or ambiguous mappings are ignored, with exact modifier matching.
    /// </summary>
    public static string? GetActionId(
        IReadOnlyDictionary<string, string>? bindings,
        Key key,
        ModifierKeys modifiers)
    {
        if (bindings is null || IsReservedGesture(key, modifiers))
        {
            return null;
        }

        var converter = new KeyGestureConverter();
        string? actionId = null;
        foreach (var binding in bindings)
        {
            KeyGesture? gesture;
            try
            {
                gesture = converter.ConvertFromInvariantString(binding.Key) as KeyGesture;
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
            {
                continue;
            }

            if (gesture?.Key != key || gesture.Modifiers != modifiers || string.IsNullOrWhiteSpace(binding.Value))
            {
                continue;
            }

            // Two spellings of the same gesture must not depend on dictionary order.
            if (actionId is not null)
            {
                return null;
            }

            actionId = binding.Value;
        }

        return actionId;
    }

    private static bool IsReservedGesture(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Windows) != 0 ||
            key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or
                Key.Escape or Key.Tab or Key.Enter or Key.Back or Key.Delete or Key.Insert or
                Key.Space or Key.Left or Key.Right or Key.Up or Key.Down or
                Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            return true;
        }

        // The document handlers accept Control with extra modifiers as well.
        if ((modifiers & ModifierKeys.Control) != 0 &&
            key is Key.S or Key.Z or Key.Y or Key.C or Key.V or Key.X or Key.A or
                Key.D or Key.N or Key.G or Key.B or Key.R or Key.F or Key.O or Key.P or Key.W)
        {
            return true;
        }

        return (key == Key.C && modifiers == ModifierKeys.None) ||
               (key == Key.F4 && (modifiers & ModifierKeys.Alt) != 0);
    }
}
