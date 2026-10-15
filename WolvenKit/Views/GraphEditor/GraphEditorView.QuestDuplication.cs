using System;
using System.Linq;
using WolvenKit.App.Interaction;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.Common;

namespace WolvenKit.Views.GraphEditor;

public partial class GraphEditorView
{
    /// <summary>Shared by the quest graph context menu and Ctrl+D; uses Nodify's current multi-selection.</summary>
    public bool DuplicateSelectedQuestNodes()
    {
        var graph = Source;
        if (graph?.GraphType != RedGraphType.Quest) return false;

        var duplicated = false;
        try
        {
            var copies = graph.DuplicateQuestNodes(Editor.SelectedItems.Cast<object>().ToArray());
            if (copies.Count == 0) return false;
            duplicated = true;

            Editor.UnselectAllConnections();
            Editor.SelectedItems.Clear();
            foreach (var copy in copies) Editor.SelectedItems.Add(copy);
            SelectedNode = copies[0];
            graph.GraphStateSave();
            return true;
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            Interactions.ShowMessageBox(duplicated
                    ? "The nodes were duplicated, but the editor could not finish updating its view. See the log for details."
                    : exception.Message,
                "Duplicate Selected Nodes", WMessageBoxButtons.Ok, WMessageBoxImage.Error);
            return duplicated;
        }
    }
}
