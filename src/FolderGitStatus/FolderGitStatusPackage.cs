using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio;
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
    [Guid(PackageGuidString)]
    public sealed class FolderGitStatusPackage : AsyncPackage
    {
        public const string PackageGuidString = "6BA26328-272B-41C6-8CED-BF383D5CCC6C";

        private GitStatusService _git;
        private SolutionExplorerDecorator _decorator;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var options = (OptionsPage)GetDialogPage(typeof(OptionsPage));
            Log.Initialize(await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow, () => options.Diagnostics);

            Log.Write($"Package loaded (VS {Application.Current?.MainWindow?.GetType().Assembly.GetName().Version}). Log: {Log.FilePath}");

            _git = new GitStatusService(() => options.GitExecutable);
            _decorator = new SolutionExplorerDecorator(_git, options, JoinableTaskFactory);

            SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution;
            SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;

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
            _decorator?.StartDiscovery();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
                SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
                _git?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
