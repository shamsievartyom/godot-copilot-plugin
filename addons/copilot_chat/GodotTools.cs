#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

/// <summary>
/// Godot editor tools exposed to the AI model via AIFunctionFactory.
/// All methods must be called on the Godot main thread — use Callable.From / CallDeferred where needed.
/// </summary>
public class GodotTools
{
    private readonly EditorPlugin _plugin;

    public GodotTools(EditorPlugin plugin)
    {
        _plugin = plugin;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Project
    // ────────────────────────────────────────────────────────────────────────

    [Description("Returns general information about the current Godot project: name, version, main scene, and project root path.")]
    public string GetProjectInfo()
    {
        var root = ProjectSettings.GlobalizePath("res://");
        var name = ProjectSettings.GetSetting("application/config/name", "").AsString();
        var mainScene = ProjectSettings.GetSetting("application/run/main_scene", "").AsString();
        var godotVersion = Engine.GetVersionInfo();
        return $"Project: {name}\nRoot: {root}\nMain scene: {mainScene}\nGodot: {godotVersion["string"]}";
    }

    [Description("Returns the full filesystem tree of the project under res://, listing all files and folders. Use maxDepth to limit traversal depth (default 4).")]
    public string GetFilesystemTree([Description("Maximum folder depth to traverse. Default is 4.")] int maxDepth = 4)
    {
        var root = ProjectSettings.GlobalizePath("res://");
        var sb = new StringBuilder();
        AppendTree(sb, root, root, 0, maxDepth);
        return sb.ToString();
    }

    [Description("Searches for files in the project whose names match the given pattern (glob-style, e.g. '*.cs', 'Player*'). Returns a list of res:// paths.")]
    public string SearchFiles([Description("Glob pattern to match file names, e.g. '*.cs' or 'Player*'.")] string pattern)
    {
        var root = ProjectSettings.GlobalizePath("res://");
        try
        {
            var files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Select(f => "res://" + f[root.Length..].Replace('\\', '/').TrimStart('/'))
                .OrderBy(f => f)
                .ToList();
            return files.Count == 0 ? "No files found." : string.Join("\n", files);
        }
        catch (Exception e)
        {
            return "Error: " + e.Message;
        }
    }

    [Description("Converts a Godot UID string (e.g. uid://abc123) to the corresponding res:// project path.")]
    public string UidToProjectPath([Description("The UID string, e.g. 'uid://abc123'.")] string uid)
    {
        var id = ResourceUid.TextToId(uid);
        if (id == ResourceUid.InvalidId) return "Invalid UID: " + uid;
        var path = ResourceUid.GetIdPath(id);
        return string.IsNullOrEmpty(path) ? "No path found for UID: " + uid : path;
    }

    [Description("Converts a res:// project path to the corresponding Godot UID string.")]
    public string ProjectPathToUid([Description("The res:// path, e.g. 'res://scenes/Main.tscn'.")] string path)
    {
        var id = ResourceLoader.GetResourceUid(path);
        if (id == ResourceUid.InvalidId) return "No UID found for path: " + path;
        return ResourceUid.IdToText(id);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Scenes
    // ────────────────────────────────────────────────────────────────────────

    [Description("Returns the live scene tree of the currently open scene in the editor, including node names, types, and nesting.")]
    public string GetSceneTree()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return "No scene is currently open in the editor.";
        var sb = new StringBuilder();
        AppendNodeTree(sb, root, 0);
        return sb.ToString();
    }

    [Description("Returns the raw text content of a .tscn or .tres scene file at the given res:// path.")]
    public string GetSceneFileContent([Description("The res:// path to the scene file, e.g. 'res://scenes/Main.tscn'.")] string path)
    {
        var abs = ProjectSettings.GlobalizePath(path);
        if (!File.Exists(abs)) return "File not found: " + path;
        return File.ReadAllText(abs);
    }

    [Description("Creates a new empty scene file at the given res:// path with a Node3D or Node2D root. Does not open it.")]
    public string CreateScene(
        [Description("The res:// path for the new scene, e.g. 'res://scenes/NewScene.tscn'.")] string path,
        [Description("Root node type: 'Node2D', 'Node3D', or 'Control'. Default is 'Node2D'.")] string rootType = "Node2D")
    {
        var abs = ProjectSettings.GlobalizePath(path);
        if (File.Exists(abs)) return "File already exists: " + path;
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var nodeName = Path.GetFileNameWithoutExtension(abs);
        var content = $"[gd_scene format=3 uid=\"uid://auto\"]\n\n[node name=\"{nodeName}\" type=\"{rootType}\"]\n";
        File.WriteAllText(abs, content);

        // Notify Godot filesystem
        Callable.From(() => EditorInterface.Singleton.GetResourceFilesystem().Scan()).CallDeferred();
        return "Created: " + path;
    }

    [Description("Opens a scene file in the Godot editor.")]
    public string OpenScene([Description("The res:// path to the scene to open.")] string path)
    {
        Callable.From(() => EditorInterface.Singleton.OpenSceneFromPath(path)).CallDeferred();
        return "Opening scene: " + path;
    }

    [Description("Deletes a scene file from the project. This is irreversible.")]
    public string DeleteScene([Description("The res:// path to the scene file to delete.")] string path)
    {
        var abs = ProjectSettings.GlobalizePath(path);
        if (!File.Exists(abs)) return "File not found: " + path;
        File.Delete(abs);
        Callable.From(() => EditorInterface.Singleton.GetResourceFilesystem().Scan()).CallDeferred();
        return "Deleted: " + path;
    }

    [Description("Starts playing the specified scene (or the current scene if path is empty) in the Godot editor.")]
    public string PlayScene([Description("The res:// path to the scene to play. Leave empty to play the current scene.")] string path = "")
    {
        Callable.From(() =>
        {
            if (string.IsNullOrEmpty(path))
                EditorInterface.Singleton.PlayCurrentScene();
            else
                EditorInterface.Singleton.PlayCustomScene(path);
        }).CallDeferred();
        return "Playing scene: " + (string.IsNullOrEmpty(path) ? "(current)" : path);
    }

    [Description("Stops the currently running scene in the Godot editor.")]
    public string StopScene()
    {
        Callable.From(() => EditorInterface.Singleton.StopPlayingScene()).CallDeferred();
        return "Stopped playing scene.";
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Nodes
    // ────────────────────────────────────────────────────────────────────────

    [Description("Adds a new node of the given type as a child of the specified parent node in the currently open scene. parentNodePath uses Godot node paths like '/root/Main' or '.' for scene root.")]
    public string AddNode(
        [Description("Godot node path to the parent node, e.g. '/root/Main' or '.' for scene root.")] string parentNodePath,
        [Description("The Godot class name of the node to create, e.g. 'Sprite2D', 'CharacterBody2D'.")] string nodeType,
        [Description("Name for the new node.")] string nodeName)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }

                var parent = parentNodePath == "." ? sceneRoot : sceneRoot.GetNodeOrNull(parentNodePath);
                if (parent == null) { result = "Parent node not found: " + parentNodePath; return; }

                var newNode = (Node)ClassDB.Instantiate(nodeType);
                if (newNode == null) { result = "Unknown node type: " + nodeType; return; }
                newNode.Name = nodeName;
                parent.AddChild(newNode);
                newNode.Owner = sceneRoot;
                result = $"Added {nodeType} '{nodeName}' to '{parentNodePath}'.";
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    [Description("Deletes a node from the currently open scene by its node path.")]
    public string DeleteNode([Description("Godot node path of the node to delete, e.g. '/root/Main/Enemy'.")] string nodePath)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }
                var node = sceneRoot.GetNodeOrNull(nodePath);
                if (node == null) { result = "Node not found: " + nodePath; return; }
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
                result = "Deleted node: " + nodePath;
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    [Description("Duplicates a node in the currently open scene and adds the copy as a sibling.")]
    public string DuplicateNode(
        [Description("Godot node path of the node to duplicate.")] string nodePath,
        [Description("Name for the duplicated node.")] string newName)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }
                var node = sceneRoot.GetNodeOrNull(nodePath);
                if (node == null) { result = "Node not found: " + nodePath; return; }
                var dup = node.Duplicate();
                dup.Name = newName;
                node.GetParent()?.AddChild(dup);
                dup.Owner = sceneRoot;
                result = $"Duplicated '{nodePath}' as '{newName}'.";
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    [Description("Moves a node to a different parent in the currently open scene.")]
    public string MoveNode(
        [Description("Godot node path of the node to move.")] string nodePath,
        [Description("Godot node path of the new parent node.")] string newParentPath)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }
                var node = sceneRoot.GetNodeOrNull(nodePath);
                if (node == null) { result = "Node not found: " + nodePath; return; }
                var newParent = newParentPath == "." ? sceneRoot : sceneRoot.GetNodeOrNull(newParentPath);
                if (newParent == null) { result = "New parent not found: " + newParentPath; return; }
                node.GetParent()?.RemoveChild(node);
                newParent.AddChild(node);
                node.Owner = sceneRoot;
                result = $"Moved '{nodePath}' to '{newParentPath}'.";
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    [Description("Updates a property on a node in the currently open scene. Value is provided as a string and will be parsed to the appropriate type.")]
    public string UpdateProperty(
        [Description("Godot node path of the node.")] string nodePath,
        [Description("Property name, e.g. 'position', 'visible', 'modulate'.")] string property,
        [Description("New value as string, e.g. '(100, 200)' for Vector2, 'true' for bool, '42' for int.")] string value)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }
                var node = sceneRoot.GetNodeOrNull(nodePath);
                if (node == null) { result = "Node not found: " + nodePath; return; }

                var current = node.Get(property);
                var variant = ParseVariant(value, current.VariantType);
                node.Set(property, variant);
                result = $"Set '{property}' = '{value}' on '{nodePath}'.";
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Scripts
    // ────────────────────────────────────────────────────────────────────────

    [Description("Returns the text content of a script file at the given res:// path.")]
    public string ViewScript([Description("The res:// path to the script file.")] string path)
    {
        var abs = ProjectSettings.GlobalizePath(path);
        if (!File.Exists(abs)) return "File not found: " + path;
        return File.ReadAllText(abs);
    }

    [Description("Creates a new C# or GDScript file at the given res:// path with optional initial content.")]
    public string CreateScript(
        [Description("The res:// path for the new script, e.g. 'res://scripts/Player.cs'.")] string path,
        [Description("Initial content for the script file.")] string content = "")
    {
        var abs = ProjectSettings.GlobalizePath(path);
        if (File.Exists(abs)) return "File already exists: " + path;
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(abs, content);
        Callable.From(() => EditorInterface.Singleton.GetResourceFilesystem().Scan()).CallDeferred();
        return "Created: " + path;
    }

    [Description("Attaches a script to a node in the currently open scene.")]
    public string AttachScript(
        [Description("Godot node path of the node to attach the script to.")] string nodePath,
        [Description("The res:// path to the script file to attach.")] string scriptPath)
    {
        string result = null;
        Callable.From(() =>
        {
            try
            {
                var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                if (sceneRoot == null) { result = "No scene open."; return; }
                var node = sceneRoot.GetNodeOrNull(nodePath);
                if (node == null) { result = "Node not found: " + nodePath; return; }
                var script = ResourceLoader.Load<Script>(scriptPath);
                if (script == null) { result = "Script not found: " + scriptPath; return; }
                node.SetScript(script);
                result = $"Attached '{scriptPath}' to '{nodePath}'.";
            }
            catch (Exception e) { result = "Error: " + e.Message; }
        }).Call();
        return result ?? "Done.";
    }

    [Description("Writes text content to a file at the given res:// path, creating or overwriting it.")]
    public string EditFile(
        [Description("The res:// path to the file.")] string path,
        [Description("The full new content to write to the file.")] string content)
    {
        var abs = ProjectSettings.GlobalizePath(path);
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(abs, content);
        Callable.From(() => EditorInterface.Singleton.GetResourceFilesystem().Scan()).CallDeferred();
        return "Written: " + path;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Editor
    // ────────────────────────────────────────────────────────────────────────

    [Description("Takes a screenshot of the Godot editor viewport and saves it to a temporary PNG file. Returns the file path.")]
    public string GetEditorScreenshot()
    {
        string resultPath = null;
        Callable.From(() =>
        {
            try
            {
                var viewport = EditorInterface.Singleton.GetEditorViewport3D()
                    ?? (Viewport)EditorInterface.Singleton.GetEditorViewport2D();
                var img = viewport.GetTexture().GetImage();
                var tmp = Path.Combine(Path.GetTempPath(), $"godot_editor_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png");
                img.SavePng(tmp);
                resultPath = tmp;
            }
            catch (Exception e) { resultPath = "Error: " + e.Message; }
        }).Call();
        return resultPath ?? "Error: could not capture screenshot.";
    }

    [Description("Takes a screenshot of the running game viewport (only works while a scene is playing) and saves it to a temporary PNG file.")]
    public string GetRunningSceneScreenshot()
    {
        if (!EditorInterface.Singleton.IsPlayingScene())
            return "No scene is currently playing.";

        string resultPath = null;
        Callable.From(() =>
        {
            try
            {
                var tree = _plugin.GetTree();
                var viewport = tree?.Root;
                if (viewport == null) { resultPath = "Error: no running scene viewport."; return; }
                var img = viewport.GetTexture().GetImage();
                var tmp = Path.Combine(Path.GetTempPath(), $"godot_game_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png");
                img.SavePng(tmp);
                resultPath = tmp;
            }
            catch (Exception e) { resultPath = "Error: " + e.Message; }
        }).Call();
        return resultPath ?? "Error: could not capture screenshot.";
    }

    [Description("Executes arbitrary GDScript source code in the editor context and returns the result as a string.")]
    public string ExecuteEditorScript([Description("GDScript source code to execute.")] string code)
    {
        return "ExecuteEditorScript is not supported in this version.";
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static void AppendTree(StringBuilder sb, string root, string path, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        var indent = new string(' ', depth * 2);
        var entries = Directory.EnumerateFileSystemEntries(path).OrderBy(e => e);
        foreach (var entry in entries)
        {
            var rel = entry[root.Length..].Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith(".godot") || rel.StartsWith(".git")) continue;
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                sb.AppendLine($"{indent}{name}/");
                AppendTree(sb, root, entry, depth + 1, maxDepth);
            }
            else
            {
                sb.AppendLine($"{indent}{name}");
            }
        }
    }

    private static void AppendNodeTree(StringBuilder sb, Node node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var scriptInfo = node.GetScript().AsGodotObject() is Script s ? $" [{s.ResourcePath}]" : "";
        sb.AppendLine($"{indent}{node.Name} ({node.GetClass()}){scriptInfo}");
        foreach (Node child in node.GetChildren())
            AppendNodeTree(sb, child, depth + 1);
    }

    private static Variant ParseVariant(string value, Variant.Type hint)
    {
        return hint switch
        {
            Variant.Type.Bool   => Variant.From(value.Trim().ToLower() is "true" or "1" or "yes"),
            Variant.Type.Int    => Variant.From(long.Parse(value.Trim())),
            Variant.Type.Float  => Variant.From(double.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture)),
            Variant.Type.Vector2 => ParseVector2(value),
            Variant.Type.Vector3 => ParseVector3(value),
            _                   => Variant.From(value),
        };
    }

    private static Variant ParseVector2(string s)
    {
        var nums = ExtractNumbers(s);
        return Variant.From(new Vector2((float)nums[0], (float)nums[1]));
    }

    private static Variant ParseVector3(string s)
    {
        var nums = ExtractNumbers(s);
        return Variant.From(new Vector3((float)nums[0], (float)nums[1], (float)nums[2]));
    }

    private static double[] ExtractNumbers(string s)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(s, @"[-+]?[0-9]*\.?[0-9]+");
        return matches.Cast<System.Text.RegularExpressions.Match>()
                      .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
                      .ToArray();
    }

    public IList<AIFunction> BuildAIFunctions()
    {
        return
        [
            AIFunctionFactory.Create(GetProjectInfo),
            AIFunctionFactory.Create(GetFilesystemTree),
            AIFunctionFactory.Create(SearchFiles),
            AIFunctionFactory.Create(UidToProjectPath),
            AIFunctionFactory.Create(ProjectPathToUid),
            AIFunctionFactory.Create(GetSceneTree),
            AIFunctionFactory.Create(GetSceneFileContent),
            AIFunctionFactory.Create(CreateScene),
            AIFunctionFactory.Create(OpenScene),
            AIFunctionFactory.Create(DeleteScene),
            AIFunctionFactory.Create(PlayScene),
            AIFunctionFactory.Create(StopScene),
            AIFunctionFactory.Create(AddNode),
            AIFunctionFactory.Create(DeleteNode),
            AIFunctionFactory.Create(DuplicateNode),
            AIFunctionFactory.Create(MoveNode),
            AIFunctionFactory.Create(UpdateProperty),
            AIFunctionFactory.Create(ViewScript),
            AIFunctionFactory.Create(CreateScript),
            AIFunctionFactory.Create(AttachScript),
            AIFunctionFactory.Create(EditFile),
            AIFunctionFactory.Create(GetEditorScreenshot),
            AIFunctionFactory.Create(GetRunningSceneScreenshot),
        ];
    }
}
#endif
