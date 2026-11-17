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
    }

    /// <summary>
    /// "Show Branch Changes" on the Solution Explorer toolbar and "Compare with Base Branch"
    /// on the context menu of a file.
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

            var compare = new OleMenuCommand(commands.ExecuteCompare, new CommandID(PackageIds.CommandSet, (int)PackageIds.CompareWithBaseId));
            compare.BeforeQueryStatus += commands.QueryCompare;
            commandService.AddCommand(compare);
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

        private void QueryCompare(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var command = (OleMenuCommand)sender;
            var path = GetSelectedFile();
            command.Visible = command.Enabled = path != null && _git.GetRepository(path) != null;
        }

        private void ExecuteCompare(object sender, EventArgs e)
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
                BaseVersion version;
                try
                {
                    await TaskScheduler.Default;
                    version = repository.GetBaseVersion(path);
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Log.Write($"Compare with base branch failed for '{path}': {ex.Message}");
                    VsShellUtilities.ShowMessageBox(_package, ex.Message, "Compare with Base Branch",
                        OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return;
                }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                OpenComparison(path, version);
            }).Task.FileAndForget("FolderGitStatus/CompareWithBase");
        }

        private void OpenComparison(string path, BaseVersion version)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var name = Path.GetFileName(path);
            var baseName = version.RenamedFrom != null ? Path.GetFileName(version.RenamedFrom.Replace('/', '\\')) : name;
            var leftLabel = version.Existed
                ? $"{baseName} @ {version.BaseRef} ({version.Commit})"
                : $"{name} @ {version.BaseRef} ({version.Commit}) - new on the branch";
            var rightLabel = $"{name} (working tree)";

            var difference = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
            difference.OpenComparisonWindow2(
                version.TempFile,
                path,
                $"{name} vs {version.BaseRef}",
                $"{path}{Environment.NewLine}compared with {version.BaseRef} at {version.Commit}",
                leftLabel,
                rightLabel,
                $"{leftLabel} → {rightLabel}",
                null,
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
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
