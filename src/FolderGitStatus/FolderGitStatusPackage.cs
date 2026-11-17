using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace FolderGitStatus
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionsPage), "Folder Git Status", "General", 0, 0, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    public sealed class FolderGitStatusPackage : AsyncPackage, IVsWindowFrameEvents, IVsInfoBarUIEvents
    {
        public const string PackageGuidString = "6BA26328-272B-41C6-8CED-BF383D5CCC6C";

        // Solution Explorer tool window
        private static readonly Guid SolutionExplorerGuid = new Guid("3AE79031-E1BC-11D0-8F78-00A0C9110057");

        private static GitStatusService _sharedGit;

        private GitStatusService _git;
        private SolutionExplorerDecorator _decorator;
        private uint _frameEventsCookie;
        private IVsUIShell7 _uiShell7;
        private IVsInfoBarUIElement _gitInfoBar;

        /// <summary>
        /// The package's Git service, loading the package if needed (for the Solution Explorer filter,
        /// which VS may create before the package loads).
        /// </summary>
        internal static async System.Threading.Tasks.Task<GitStatusService> GetGitServiceAsync(CancellationToken cancellationToken)
        {
            if (_sharedGit == null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
                {
                    var guid = new Guid(PackageGuidString);
                    shell.LoadPackage(ref guid, out _);
                }
            }

            return _sharedGit ?? throw new InvalidOperationException("Folder Git Status is not loaded");
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var options = (OptionsPage)GetDialogPage(typeof(OptionsPage));
            Log.Initialize(await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow, () => options.Diagnostics);

            var sinceStartup = DateTime.Now - Process.GetCurrentProcess().StartTime;
            Log.Write($"Package loaded {sinceStartup.TotalSeconds:0}s after VS started " +
                $"(VS {Application.Current?.MainWindow?.GetType().Assembly.GetName().Version}). Log: {Log.FilePath}");
            Log.Write($"Options: Enabled={options.Enabled}, ColorizeFiles={options.ColorizeFiles}, Badge={options.Badge}, " +
                $"ShowBranchChanges={options.ShowBranchChanges}, BaseBranch=\"{options.BaseBranch}\", DimIgnoredItems={options.DimIgnoredItems}, " +
                $"ItalicStaged={options.ItalicStaged}, Diagnostics={options.Diagnostics}");

            GitProcess.Unavailable += OnGitUnavailable;
            GitProcess.Available += OnGitAvailable;
            _git = new GitStatusService(options);
            _sharedGit = _git;
            options.Applied += (s, e) => _git.Refresh();
            _decorator = new SolutionExplorerDecorator(_git, options, JoinableTaskFactory);

            await Commands.InitializeAsync(this, options, _git);

            // A "New Solution Explorer View" window has its own tree.
            _uiShell7 = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell7;
            if (_uiShell7 != null)
            {
                _frameEventsCookie = _uiShell7.AdviseWindowFrameEvents(this);
            }

            // The package loads in the background while the solution opens, so these events may
            // have fired already; the decorator's own discovery covers that case.
            SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;
            SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
            SolutionEvents.OnAfterBackgroundSolutionLoadComplete += OnAfterBackgroundSolutionLoadComplete;
            SolutionEvents.OnAfterOpenFolder += OnAfterOpenFolder;

            if (options.Diagnostics)
            {
                ProbeSolutionExplorer();
            }
        }

        /// <summary>
        /// Logs the Solution Explorer structure and statistics shortly after loading (only with diagnostics on).
        /// </summary>
        private void ProbeSolutionExplorer()
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                var elapsed = 0;
                foreach (var delay in new[] { 10, 20 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), DisposalToken);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                    elapsed += delay;
                    SolutionExplorerProbe.Dump($"{elapsed}s after loading");
                    _decorator.LogStatistics();
                }
            }).Task.FileAndForget("FolderGitStatus/Probe");
        }

        private void OnAfterCloseSolution(object sender, EventArgs e) => _git?.Reset();

        private void OnAfterOpenSolution(object sender, OpenSolutionEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _decorator?.StartDiscovery("solution opened");
        }

        private void OnAfterBackgroundSolutionLoadComplete(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _decorator?.StartDiscovery("solution load completed");
        }

        private void OnAfterOpenFolder(object sender, FolderEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _decorator?.StartDiscovery("folder opened");
        }

        #region Git not found

        /// <summary>
        /// Without git nothing is colored, which looks like the extension is broken; an info bar
        /// says why and links to the option with the path to git.exe.
        /// </summary>
        private void OnGitUnavailable(object sender, string message)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                if (_gitInfoBar != null ||
                    !(await GetServiceAsync(typeof(SVsShell)) is IVsShell shell) ||
                    ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject)) ||
                    !(hostObject is IVsInfoBarHost host) ||
                    !(await GetServiceAsync(typeof(SVsInfoBarUIFactory)) is IVsInfoBarUIFactory factory))
                {
                    return;
                }

                var model = new InfoBarModel(
                    new[] { new InfoBarTextSpan($"Folder Git Status could not run git, so Solution Explorer is not colored. {message} ") },
                    new[] { new InfoBarHyperlink("Set the path to git.exe") },
                    KnownMonikers.StatusWarning,
                    isCloseButtonVisible: true);

                _gitInfoBar = factory.CreateInfoBar(model);
                _gitInfoBar.Advise(this, out _);
                host.AddInfoBar(_gitInfoBar);
            }).Task.FileAndForget("FolderGitStatus/GitInfoBar");
        }

        private void OnGitAvailable(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                _gitInfoBar?.Close();
                Log.Write("git runs again");
            }).Task.FileAndForget("FolderGitStatus/GitInfoBarClose");
        }

        void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            _gitInfoBar = null;
        }

        void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            infoBarUIElement.Close();
            ShowOptionPage(typeof(OptionsPage));
        }

        #endregion

        #region Window frame events

        void IVsWindowFrameEvents.OnFrameIsOnScreenChanged(IVsWindowFrame frame, bool newIsOnScreen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (newIsOnScreen && IsSolutionExplorer(frame))
            {
                _decorator?.ScanForNewTrees("Solution Explorer window shown");
            }
        }

        void IVsWindowFrameEvents.OnFrameCreated(IVsWindowFrame frame)
        {
        }

        void IVsWindowFrameEvents.OnFrameDestroyed(IVsWindowFrame frame)
        {
        }

        void IVsWindowFrameEvents.OnFrameIsVisibleChanged(IVsWindowFrame frame, bool newIsVisible)
        {
        }

        void IVsWindowFrameEvents.OnActiveFrameChanged(IVsWindowFrame oldFrame, IVsWindowFrame newFrame)
        {
        }

        private static bool IsSolutionExplorer(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return frame != null &&
                ErrorHandler.Succeeded(frame.GetGuidProperty((int)__VSFPROPID.VSFPROPID_GuidPersistenceSlot, out var guid)) &&
                guid == SolutionExplorerGuid;
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
                SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
                SolutionEvents.OnAfterBackgroundSolutionLoadComplete -= OnAfterBackgroundSolutionLoadComplete;
                SolutionEvents.OnAfterOpenFolder -= OnAfterOpenFolder;
                GitProcess.Unavailable -= OnGitUnavailable;
                GitProcess.Available -= OnGitAvailable;
                if (_frameEventsCookie != 0)
                {
#pragma warning disable VSTHRD010 // packages are disposed on the UI thread
                    _uiShell7?.UnadviseWindowFrameEvents(_frameEventsCookie);
#pragma warning restore VSTHRD010
                }

                _git?.Dispose();
                _sharedGit = null;
            }

            base.Dispose(disposing);
        }
    }
}
