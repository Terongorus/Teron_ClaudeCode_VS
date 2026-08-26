using Newtonsoft.Json.Linq;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// The `tools/list` response for <see cref="IdeCompanionServer"/> - 11 of the real official
    /// extension's 12 tools (schemas confirmed live 2026-08-26 against a running instance),
    /// omitting the Jupyter-only `executeCode` (no VS equivalent).
    /// </summary>
    internal static class ToolSchemas
    {
        public static JArray BuildToolList() => new JArray
        {
            Tool("openDiff", "Open a diff view comparing the current and proposed contents of a file",
                Obj(
                    Prop("old_file_path", "string", "Path to the file to show diff for. If not provided, uses active editor."),
                    Prop("new_file_path", "string", "Path to the file to show diff for. If not provided, uses active editor."),
                    Prop("new_file_contents", "string", "Contents of the new file."),
                    Prop("tab_name", "string", "Title for the diff tab.")),
                required: new[] { "old_file_path", "new_file_path", "new_file_contents", "tab_name" }),

            Tool("getDiagnostics", "Get language diagnostics from Visual Studio's Error List",
                Obj(Prop("uri", "string", "Optional file URI to get diagnostics for. If not provided, gets diagnostics for all files.")),
                required: null),

            Tool("close_tab", null,
                Obj(Prop("tab_name", "string", null)),
                required: new[] { "tab_name" }),

            Tool("closeAllDiffTabs", "Close all diff tabs in the editor", Obj(), required: null),

            Tool("openFile", "Open a file in the editor and optionally select a range of text",
                Obj(
                    Prop("filePath", "string", "Path to the file to open"),
                    Prop("preview", "boolean", "Whether to open the file in preview mode"),
                    Prop("startText", "string", "Text marking the start of the range to select"),
                    Prop("endText", "string", "Text marking the end of the range to select"),
                    Prop("selectToEndOfLine", "boolean", "Extend the selection to the end of the line"),
                    Prop("makeFrontmost", "boolean", "Bring the opened editor to the foreground")),
                required: new[] { "filePath" }),

            Tool("getOpenEditors", "Get information about currently open editors", Obj(), required: null),

            Tool("getWorkspaceFolders", "Get all workspace folders currently open in the IDE", Obj(), required: null),

            Tool("getCurrentSelection", "Get the current text selection in the active editor", Obj(), required: null),

            Tool("checkDocumentDirty", "Check if a document has unsaved changes (is dirty)",
                Obj(Prop("filePath", "string", null)), required: new[] { "filePath" }),

            Tool("saveDocument", "Save a document with unsaved changes",
                Obj(Prop("filePath", "string", null)), required: new[] { "filePath" }),

            Tool("getLatestSelection", "Get the most recent text selection (even if not in the active editor)", Obj(), required: null),
        };

        private static JObject Tool(string name, string? description, JObject properties, string[]? required)
        {
            var inputSchema = new JObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required != null)
                inputSchema["required"] = new JArray(required);

            var tool = new JObject { ["name"] = name, ["inputSchema"] = inputSchema };
            if (description != null)
                tool["description"] = description;
            return tool;
        }

        private static JObject Obj(params (string name, JObject schema)[] props)
        {
            var obj = new JObject();
            foreach (var (name, schema) in props)
                obj[name] = schema;
            return obj;
        }

        private static (string, JObject) Prop(string name, string type, string? description)
        {
            var schema = new JObject { ["type"] = type };
            if (description != null)
                schema["description"] = description;
            return (name, schema);
        }
    }
}
