using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace FolderGitStatus
{
    /// <summary>
    /// What is shown after the name of a changed item.
    /// </summary>
    public enum BadgeStyle
    {
        /// <summary>Only the color.</summary>
        None,

        /// <summary>M, A or C (modified, added, conflict).</summary>
        Letter,

        /// <summary>Folders: the number of changed files inside; files: the letter.</summary>
        Count,
    }

    /// <summary>
    /// Tools > Options > Folder Git Status > General.
    /// </summary>
    [Guid("19B2100C-84A4-436B-B1F8-FDB736D1862F")]
    public sealed class OptionsPage : DialogPage
    {
        [Category("General")]
        [DisplayName("Enabled")]
        [Description("Colors folders, projects and solution folders that contain Git changes.")]
        public bool Enabled { get; set; } = true;

        [Category("General")]
        [DisplayName("Colorize files")]
        [Description("Also colors the names of changed files using the same scheme as folders (in addition to the native Visual Studio glyph).")]
        public bool ColorizeFiles { get; set; } = true;

        [Category("General")]
        [DisplayName("Bold")]
        [Description("Shows items that contain changes in bold.")]
        public bool UseBold { get; set; }

        [Category("General")]
        [DisplayName("Badge")]
        [Description("Shows a badge after the name of changed items, so the status does not depend on the color alone. " +
            "Letter: M (modified), A (added) or C (conflict). Count: the number of changed files inside each folder (files show the letter).")]
        [DefaultValue(BadgeStyle.None)]
        public BadgeStyle Badge { get; set; } = BadgeStyle.None;

        [Category("General")]
        [DisplayName("Summary tooltip")]
        [Description("Hovering a changed folder or file shows how many files are modified, new or in conflict, uncommitted and committed on the branch.")]
        public bool ShowTooltip { get; set; } = true;

        [Category("General")]
        [DisplayName("Staged changes in italic")]
        [Description("Shows in italic the items whose changes are all staged (in the index), to tell them apart from changes not staged yet.")]
        public bool ItalicStaged { get; set; }

        [Category("General")]
        [DisplayName("Dim ignored items")]
        [Description("Shows the items ignored by Git (.gitignore) dimmed, e.g. local settings files or bin/obj with \"Show All Files\".")]
        public bool DimIgnoredItems { get; set; }

        [Category("Colors")]
        [DisplayName("Modified")]
        [Description("Color for items that contain modified, deleted or renamed files.")]
        public Color ModifiedColor { get; set; } = Color.FromArgb(0xE5, 0x53, 0x4B);

        [Category("Colors")]
        [DisplayName("Added / untracked")]
        [Description("Color for items that contain only new files (added or untracked).")]
        public Color AddedColor { get; set; } = Color.FromArgb(0x3F, 0xB9, 0x50);

        [Category("Colors")]
        [DisplayName("Conflict")]
        [Description("Color for items that contain files with merge conflicts.")]
        public Color ConflictColor { get; set; } = Color.FromArgb(0xD2, 0x99, 0x22);

        [Category("Colors")]
        [DisplayName("Committed changes opacity (%)")]
        [Description("Opacity of the color for items whose changes are only in commits of the current branch (see \"Show branch changes\"), from 10 to 100.")]
        public int CommittedOpacity { get; set; } = 55;

        [Category("Branch")]
        [DisplayName("Show branch changes")]
        [Description("Also colors the items changed by the commits of the current branch that are not in the base branch, in a lighter tone. " +
            "Uncommitted changes keep the full color. Also available as a button on the Solution Explorer toolbar.")]
        public bool ShowBranchChanges { get; set; }

        [Category("Branch")]
        [DisplayName("Base branch")]
        [Description("Branch to compare with, e.g. \"origin/main\" or \"develop\". Applies to every repository; where it does not exist, or when left empty, " +
            "the remote's default branch (origin/HEAD) is used, or else origin/main, origin/master, main or master. " +
            "A repository can set its own with \"git config foldergitstatus.baseBranch <branch>\", which takes precedence.")]
        public string BaseBranch { get; set; } = "";

        [Category("Advanced")]
        [DisplayName("Git executable")]
        [Description("Path to git.exe. Use just \"git\" if it is on the PATH.")]
        public string GitExecutable { get; set; } = "git";

        [Category("Advanced")]
        [DisplayName("Git timeout (seconds)")]
        [Description("A git command that takes longer than this is stopped, so a stuck git does not stop the updates (minimum 5).")]
        public int GitTimeoutSeconds { get; set; } = 30;

        [Category("Advanced")]
        [DisplayName("Ignored folders")]
        [Description("Folders whose changes do not trigger a refresh, separated by ';' (e.g. build output that changes all the time). " +
            "They are still colored if Git reports changes in them.")]
        public string IgnoredFolders { get; set; } = GitOutput.DefaultIgnoredFolders;

        [Category("Advanced")]
        [DisplayName("Diagnostic log")]
        [Description("Writes details to the \"Folder Git Status\" pane of the Output window and to %LOCALAPPDATA%\\FolderGitStatus\\log.txt.")]
        public bool Diagnostics { get; set; }

        public event EventHandler Applied;

        /// <summary>
        /// Saves a change made outside the dialog (e.g. by a toolbar button) and notifies like "OK" does.
        /// </summary>
        public void ApplyChanges()
        {
            SaveSettingsToStorage();
            Applied?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);

            if (e.ApplyBehavior == ApplyKind.Apply)
            {
                Applied?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
