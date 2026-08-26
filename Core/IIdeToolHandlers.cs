using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// Implements the 11 VS-relevant tools exposed by the IDE companion MCP server (see
    /// <see cref="IdeCompanionServer"/>). Kept separate from the WebSocket/JSON-RPC transport so
    /// the transport layer can be exercised with a fake implementation independent of a running
    /// VS host - <see cref="VsIdeToolHandlers"/> is the real, VS SDK-backed implementation.
    /// Each method returns the tool's raw JSON payload; <see cref="IdeCompanionServer"/> wraps it
    /// in the standard MCP `{"content":[{"type":"text","text":"&lt;json&gt;"}]}` envelope.
    /// </summary>
    public interface IIdeToolHandlers
    {
        Task<JObject> GetWorkspaceFoldersAsync();
        Task<JObject> GetOpenEditorsAsync();
        Task<JObject> GetCurrentSelectionAsync();
        Task<JObject> GetLatestSelectionAsync();
        Task<JObject> CheckDocumentDirtyAsync(string filePath);
        Task<JObject> SaveDocumentAsync(string filePath);
        Task<JObject> OpenFileAsync(string filePath, bool preview, string? startText, string? endText, bool selectToEndOfLine, bool makeFrontmost);
        Task<JObject> CloseTabAsync(string tabName);
        Task<JObject> CloseAllDiffTabsAsync();

        /// <summary>Returns a JSON array (matching the real server's top-level-array shape), not an object.</summary>
        Task<JArray> GetDiagnosticsAsync(string? uri);

        /// <summary>
        /// Opens a native diff view comparing the old/new content and blocks until the user
        /// accepts or rejects it. Returns the same two-element content array shape the real
        /// server uses: ["FILE_SAVED", &lt;final text&gt;] on accept, ["DIFF_REJECTED", tabName]
        /// on reject - callers build the MCP envelope directly from this rather than going through
        /// the generic single-payload wrapping the other tools use.
        /// </summary>
        Task<(string status, string detail)> OpenDiffAsync(string oldFilePath, string newFilePath, string newFileContents, string tabName);
    }
}
