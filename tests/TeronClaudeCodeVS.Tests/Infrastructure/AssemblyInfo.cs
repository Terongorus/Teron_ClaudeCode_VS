using Xunit;

// These tests share process-wide state that cannot be parallelised safely:
//
//   * SessionTitleRefreshTests redirects SessionHistoryStore's static path field into a sandbox,
//     while other tests read the real history file through the same field.
//   * PluginPanelCliTests sets CLAUDE_CONFIG_DIR for the child `claude` processes it spawns; an
//     environment variable belongs to the whole process, not to one test.
//   * The WPF tests each own an STA thread and pump their own dispatcher.
//
// Run them one at a time. The whole assembly finishes in seconds, so there is nothing to gain by
// racing them and a class of intermittent failure to lose.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
