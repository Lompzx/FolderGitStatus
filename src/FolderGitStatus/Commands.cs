using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace FolderGitStatus
{
    internal static class PackageIds
    {
        public const string CommandSetString = "0D3E4B8C-5B0A-4E3B-9F1C-7A2F6C1D8E41";
        public static readonly Guid CommandSet = new Guid(CommandSetString);

        public const uint ToggleBranchChangesId = 0x0100;
        public const uint BranchChangesFilterId = 0x0101;
        public const uint CompareWithBaseId = 0x0102;
        public const uint CompareWithLastCommitId = 0x0103;
        public const uint CompareWithPreviousCommitId = 0x0104;
    }

    /// <summary>
    /// "Show Branch Changes" on the Solution Explorer toolbar, and the comparison commands on the
    /// context menu of a file: with the base branch, with the last commit, and the changes of the
    /// previous commit.
    /// </summary>
    internal sealed class Commands
    {
        private const string ToggleText = "Show Branch Changes";

        private readonly AsyncPackage _package;
        private readonly OptionsPage _options;
        private readonly GitStatusService _git;

        private Commands(AsyncPackage package, OptionsPage options, GitStatusService git)
        {
            _package = package;
            _options = options;
            _git = git;
        }

        public static async Task InitializeAsync(AsyncPackage package, OptionsPage options, GitStatusService git)
        {
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                Log.Write("Menu command service not available; toolbar and context menu commands are disabled");
                return;
            }

            var commands = new Commands(package, options, git);

            var toggle = new OleMenuCommand(commands.ExecuteToggle, new CommandID(PackageIds.CommandSet, (int)PackageIds.ToggleBranchChangesId));
            toggle.BeforeQueryStatus += commands.QueryToggle;
            commandService.AddCommand(toggle);

            var compare = new OleMenuCommand(commands.ExecuteCompareWithBase, new CommandID(PackageIds.CommandSet, (int)PackageIds.CompareWithBaseId));
            compare.BeforeQueryStatus += commands.QueryCompareWithBase;
            commandService.AddCommand(compare);

            var lastCommit = new OleMenuCommand(commands.ExecuteCompareWithLastCommit, new CommandID(PackageIds.CommandSet, (int)PackageIds.CompareWithLastCommitId));
            lastCommit.BeforeQueryStatus += commands.QueryCompareWithLastCommit;
            commandService.AddCommand(lastCommit);

            var previousCommit = new OleMenuCommand(commands.ExecuteCompareWithPreviousCommit, new CommandID(PackageIds.CommandSet, (int)PackageIds.CompareWithPreviousCommitId));
            previousCommit.BeforeQueryStatus += commands.QueryCompareWithPreviousCommit;
            commandService.AddCommand(previousCommit);
        }

        private void QueryToggle(object sender, EventArgs e)
        {
            var command = (OleMenuCommand)sender;
            command.Checked = _options.ShowBranchChanges;
            command.Enabled = _options.Enabled;

            var bases = _git.GetBaseBranches();
            command.Text = _options.ShowBranchChanges && bases.Count > 0
                ? $"{ToggleText} (comparing with {string.Join(", ", bases)})"
                : ToggleText;
        }

        private void ExecuteToggle(object sender, EventArgs e)
        {
            _options.ShowBranchChanges = !_options.ShowBranchChanges;
            Log.Write($"Show branch changes {(_options.ShowBranchChanges ? "on" : "off")} (toolbar)");
            _options.ApplyChanges();
        }

        private void QueryCompareWithBase(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            var path = GetSelectedFile();
            command.Visible = command.Enabled = path != null && _git.GetRepository(path) != null;
        }

        /// <summary>
        /// Only for files with uncommitted changes: otherwise the diff would be empty.
        /// </summary>
        private void QueryCompareWithLastCommit(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            var path = GetSelectedFile();
            var repository = path != null ? _git.GetRepository(path) : null;
            command.Visible = command.Enabled = repository?.Snapshot.GetFile(path) != null;
        }

        /// <summary>
        /// Not for new files (untracked or only added to the index): they have no commit yet.
        /// </summary>
        private void QueryCompareWithPreviousCommit(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            var path = GetSelectedFile();
            var repository = path != null ? _git.GetRepository(path) : null;
            command.Visible = command.Enabled = repository != null &&
                repository.Snapshot.GetFile(path)?.Kind != GitStatus.Added &&
                !repository.Snapshot.IsIgnored(path);
        }

        private void ExecuteCompareWithBase(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Compare("Compare with Base Branch", (repository, path) =>
            {
                var version = repository.GetBaseVersion(path);
                return () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    OpenWithWorkingTree(path, version,
                        caption: $"{Path.GetFileName(path)} vs {version.Label}",
                        description: $"compared with {version.Label} at {version.Commit}",
                        missing: "new on the branch");
                };
            });
        }

        private void ExecuteCompareWithLastCommit(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Compare("Compare with Last Commit", (repository, path) =>
            {
                var version = repository.GetHeadVersion(path);
                return () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    OpenWithWorkingTree(path, version,
                        caption: $"{Path.GetFileName(path)} vs last commit",
                        description: $"compared with the last commit of {version.Label} ({version.Commit})",
                        missing: "not committed yet");
                };
            });
        }

        private void ExecuteCompareWithPreviousCommit(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Compare("Compare with Previous Commit", (repository, path) =>
            {
                var change = repository.GetLastCommitChange(path);
                return () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    OpenCommitChange(path, change);
                };
            });
        }

        /// <summary>
        /// Runs <paramref name="prepare"/> (which calls git) on a background thread for the selected
        /// file, then the action it returns on the UI thread. Errors are shown in a message box.
        /// </summary>
        private void Compare(string title, Func<GitRepository, string, Action> prepare)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var path = GetSelectedFile();
            var repository = path != null ? _git.GetRepository(path) : null;
            if (repository == null)
            {
                return;
            }

            path = PathUtil.GetActualCase(path);

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                Action open;
                try
                {
                    await TaskScheduler.Default;
                    open = prepare(repository, path);
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Log.Write($"{title} failed for '{path}': {ex.Message}");
                    VsShellUtilities.ShowMessageBox(_package, ex.Message, title,
                        OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return;
                }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                open();
            }).Task.FileAndForget("FolderGitStatus/" + title.Replace(" ", string.Empty));
        }

        /// <param name="missing">Shown on the left side when the file is not in that commit.</param>
        private static void OpenWithWorkingTree(string path, FileVersion version, string caption, string description, string missing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var name = Path.GetFileName(path);
            var leftLabel = version.Existed
                ? $"{NameIn(version, name)} @ {version.Label} ({version.Commit})"
                : $"{name} @ {version.Label} ({version.Commit}) - {missing}";
            var rightLabel = $"{name} (working tree)";

            OpenDiff(version.TempFile, path, caption, $"{path}{Environment.NewLine}{description}",
                leftLabel, rightLabel, __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
        }

        private static void OpenCommitChange(string path, CommitChange change)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var name = Path.GetFileName(path);
            var commit = change.After.Commit;
            var leftLabel = change.Before.Existed
                ? $"{NameIn(change.Before, name)} @ {change.Before.Commit} (before {commit})"
                : $"{NameIn(change.After, name)} - created in {commit}";
            var rightLabel = $"{NameIn(change.After, name)} @ {commit}: {change.Subject}";

            OpenDiff(change.Before.TempFile, change.After.TempFile, $"{name} @ {commit}",
                $"{path}{Environment.NewLine}changes of commit {commit}: {change.Subject}",
                leftLabel, rightLabel,
                __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);
        }

        /// <summary>
        /// The file name in that version: the original one when the file was renamed since.
        /// </summary>
        private static string NameIn(FileVersion version, string name) =>
            version.RenamedFrom != null ? Path.GetFileName(version.RenamedFrom.Replace('/', '\\')) : name;

        private static void OpenDiff(string left, string right, string caption, string tooltip,
            string leftLabel, string rightLabel, __VSDIFFSERVICEOPTIONS options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var difference = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
            difference.OpenComparisonWindow2(left, right, caption, tooltip, leftLabel, rightLabel,
                $"{leftLabel} → {rightLabel}", null, (uint)options);
        }

        /// <summary>
        /// Path of the single file selected in Solution Explorer, or null.
        /// </summary>
        private static string GetSelectedFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var selection = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
            if (selection == null ||
                ErrorHandler.Failed(selection.GetCurrentSelection(out var hierarchyPointer, out var itemId, out var multiSelect, out var containerPointer)))
            {
                return null;
            }

            try
            {
                if (hierarchyPointer == IntPtr.Zero || multiSelect != null || itemId == VSConstants.VSITEMID_SELECTION)
                {
                    return null;
                }

                if (!(Marshal.GetObjectForIUnknown(hierarchyPointer) is IVsHierarchy hierarchy) ||
                    ErrorHandler.Failed(hierarchy.GetCanonicalName(itemId, out var name)))
                {
                    return null;
                }

                var path = PathUtil.Normalize(name);
                return path != null && File.Exists(path) ? path : null;
            }
            finally
            {
                if (hierarchyPointer != IntPtr.Zero)
                {
                    Marshal.Release(hierarchyPointer);
                }

                if (containerPointer != IntPtr.Zero)
                {
                    Marshal.Release(containerPointer);
                }
            }
        }
    }
}
