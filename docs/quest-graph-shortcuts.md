# Quest and scene graph creation shortcuts

The shared graph editor resolves keyboard gestures to existing action-palette items.
It executes the same delegate as selecting that item in "All Actions for Quest Graph"
or "All Actions for Scene Graph". Nodes are placed at the current mouse position,
using the editor's existing pan/zoom coordinate conversion.

## Configuration

With WolvenKit closed, edit the existing settings file:

    %APPDATA%\REDModding\WolvenKit\config.json

Add or replace the top-level GraphActionShortcuts property, then restart WolvenKit.
Preserve the other settings in that file.

    "GraphActionShortcuts": {
      "Ctrl+Alt+1": "node:questPauseConditionNodeDefinition",
      "Ctrl+Alt+2": "node:questFactsDBManagerNodeDefinition",
      "Ctrl+Alt+3": "node:questJournalNodeDefinition",
      "Ctrl+Alt+4": "node:questAudioNodeDefinition",
      "Ctrl+Shift+K": "template:questAudioNodeDefinition:user:Alert%20Phase"
    }

The first four bindings are the defaults when the property is absent. The final
entry is an example requiring an existing user Audio template named "Alert Phase".
An explicit empty object disables all creation shortcuts. Settings saved by this
build include the map; existing settings need no version migration.

Action IDs:

| ID format | Palette action |
| --- | --- |
| node:<RED type name> | Main node entry, using its current default template |
| template:<RED type name>:user:<escaped name> | Exact user template variant |
| template:<RED type name>:system:<escaped name> | Exact system template variant |
| template:<RED type name>:raw | "No Template" variant |
| preset:scene:DebugWarning | Existing scene UIManager (Debug Warning) preset |

Use the RED class name shown by the palette, such as questAudioNodeDefinition or
scnStartNode. Template names are case-sensitive and URI-escaped (a space becomes
%20). User and system templates with the same name have different IDs. File paths
are not part of the ID. A renamed template needs an updated binding.

The main node entry retains the palette's preference: user "default", then system
"default", then raw creation. Explicit variants never fall back to another source.
Raw variant bindings are available wherever the palette offers template variants;
for a type with no templates, use its main node ID to create the raw node.

Use standard WPF KeyGesture strings, such as Ctrl+Shift+K or Ctrl+Alt+5. Matching
requires the exact modifiers. Top-row digits and numpad digits are different keys.
Malformed entries, empty action IDs, and duplicate spellings of one gesture are
ignored. Unavailable actions (including deleted templates or a scene-only action
in a quest graph) do nothing.

Standard editing, save, clipboard, document and graph-navigation shortcuts are
reserved even when extra modifiers are present. Navigation keys, Windows-key
gestures and Alt+F4 are also reserved; see GraphActionShortcuts.IsReservedGesture.

The graph must have keyboard focus and the pointer must be over that editor.
Shortcuts do not fire in text editors, combo boxes, an open action palette or
context menu, or on key repeat. Behavior graphs do not use this mapping.

For programmatic changes, replace ISettingsManager.GraphActionShortcuts to raise
the existing autosave notification. Mutating the dictionary in place requires an
explicit SettingsManager.Save().

## Implementation flow

1. GraphEditorView.Editor_OnPreviewKeyDown checks graph/focus/input conditions.
2. GraphActionShortcuts.GetActionId parses the configured gesture and rejects conflicts.
3. The view refreshes the current template-registry snapshot and calls its existing
   CreateGraphActionPaletteItems at GetMousePositionInGraph(Editor).
4. The matching root item or template variant executes GraphActionPaletteItem.TryExecute.
   The palette's ExecuteSelectedItem uses that same method and delegate.
5. Existing CreateAndSelectNode/CreateNode call RedGraph.CreateQuestNode or CreateSceneNode;
   selection, templates, sockets and dirty-state behavior remain in those existing paths.

Mappings resolve against the current displayed graph, including nested quest graphs.
Template-registry reloads are reflected on the next shortcut invocation; this does
not add a separate file watcher or reload templates from disk independently.

## Build and tests

Use the .NET SDK selected by global.json and the Windows desktop build dependencies.
From the repository root:

    dotnet build .\WolvenKit\WolvenKit.csproj -c Debug -p:Platform=x64
    dotnet test .\Tests\WolvenKit.UnitTests\WolvenKit.UnitTests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~.App.GraphEditor"
    dotnet test .\Tests\WolvenKit.IntegrationTests\WolvenKit.IntegrationTests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GraphActionShortcut"

The focused automated tests cover gesture conflicts, exact modifiers, malformed
and ambiguous mappings, stable variant IDs, defaults/disabled configuration,
settings persistence, and shared enabled/disabled palette execution.

Manual UI checks:

- Click the quest canvas, move the pointer and press Ctrl+Alt+1. Repeat after pan
  and zoom; the new node's location should match the pointer each time.
- Check both a root and a nested quest graph, then a scene graph. Scene quest-node
  wrapping should follow the existing palette behavior.
- Configure an explicit user/system template and compare its created node to the
  same palette variant. Reload the template registry and repeat.
- Focus a comment textbox or property editor, open the palette, move the pointer
  outside the graph, and hold a creation key. Check input guards and repeat suppression.
  Also pan or drag outside the editor while holding mouse capture; a creation shortcut
  must not create a node outside the viewport.
- Verify Ctrl+S, Ctrl+Z, Ctrl+C, Ctrl+V and the existing Ctrl+N palette shortcut.

Focus routing and physical mouse placement still require these manual UI checks;
the focused automated tests do not drive the desktop pointer or foreground window.
