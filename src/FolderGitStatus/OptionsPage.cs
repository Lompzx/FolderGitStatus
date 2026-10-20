using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace FolderGitStatus
{
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

        [Category("Advanced")]
        [DisplayName("Git executable")]
        [Description("Path to git.exe. Use just \"git\" if it is on the PATH.")]
        public string GitExecutable { get; set; } = "git";

        [Category("Advanced")]
        [DisplayName("Diagnostic log")]
        [Description("Writes details to the \"Folder Git Status\" pane of the Output window and to %LOCALAPPDATA%\\FolderGitStatus\\log.txt.")]
        public bool Diagnostics { get; set; }

        public event EventHandler Applied;

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
