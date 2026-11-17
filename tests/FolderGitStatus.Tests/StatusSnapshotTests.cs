using Xunit;

namespace FolderGitStatus.Tests
{
    public class StatusSnapshotTests
    {
        private const string Root = @"C:\repo";

        private static string Z(params string[] entries) => string.Join("\0", entries) + "\0";

        [Fact]
        public void Modified_file_propagates_to_every_folder_up_to_the_root()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z(" M src/App/Orders/Order.cs"));

            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\src\App\Orders\Order.cs"));
            Assert.Equal(GitStatus.Modified, snapshot.GetDirectoryStatus(@"C:\repo\src\App\Orders"));
            Assert.Equal(GitStatus.Modified, snapshot.GetDirectoryStatus(@"C:\repo\src"));
            Assert.Equal(GitStatus.Modified, snapshot.GetDirectoryStatus(Root));
            Assert.Equal(GitStatus.None, snapshot.GetDirectoryStatus(@"C:\repo\tests"));
        }

        [Fact]
        public void Paths_are_case_insensitive()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z(" M Src/App.cs"));

            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"c:\REPO\src\app.cs"));
        }

        [Theory]
        [InlineData("?? a.cs", (int)GitStatus.Added)]
        [InlineData("A  a.cs", (int)GitStatus.Added)]
        [InlineData(" M a.cs", (int)GitStatus.Modified)]
        [InlineData(" D a.cs", (int)GitStatus.Modified)]
        [InlineData("UU a.cs", (int)GitStatus.Conflict)]
        [InlineData("AA a.cs", (int)GitStatus.Conflict)]
        [InlineData("DD a.cs", (int)GitStatus.Conflict)]
        [InlineData("AU a.cs", (int)GitStatus.Conflict)]
        public void Classifies_porcelain_codes(string entry, int expected)
        {
            Assert.Equal((GitStatus)expected, StatusSnapshot.Parse(Root, Z(entry)).GetFileStatus(@"C:\repo\a.cs"));
        }

        [Fact]
        public void Most_severe_status_wins_in_a_folder_and_counts_each_kind()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z("?? src/New.cs", " M src/Old.cs", " M src/Other.cs", "UU src/Merge.cs"));
            var src = snapshot.GetDirectory(@"C:\repo\src");

            Assert.Equal(GitStatus.Conflict, src.Kind);
            Assert.Equal(1, src.Added);
            Assert.Equal(2, src.Modified);
            Assert.Equal(1, src.Conflict);
            Assert.Equal(4, src.FileCount);
            Assert.Equal("1 with conflicts, 2 modified, 1 new", src.Describe());
        }

        [Fact]
        public void Rename_uses_the_new_path_and_skips_the_source_entry()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z("R  src/New.cs", "src/Old.cs", " M other.cs"));

            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\src\New.cs"));
            Assert.Equal(GitStatus.None, snapshot.GetFileStatus(@"C:\repo\src\Old.cs"));
            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\other.cs"));
        }

        [Fact]
        public void Non_ascii_paths_are_kept()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z(" M docs/Configuração/ação.md"));

            Assert.Equal(GitStatus.Modified, snapshot.GetDirectoryStatus(@"C:\repo\docs\Configuração"));
        }

        [Fact]
        public void Staged_only_when_every_change_is_in_the_index()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z("M  staged/A.cs", "A  staged/B.cs", "MM mixed/C.cs", "M  partly/D.cs", " M partly/E.cs", "?? new/F.cs"));

            Assert.True(snapshot.GetFile(@"C:\repo\staged\A.cs").Staged);
            Assert.True(snapshot.GetDirectory(@"C:\repo\staged").Staged);
            Assert.False(snapshot.GetFile(@"C:\repo\mixed\C.cs").Staged);
            Assert.False(snapshot.GetDirectory(@"C:\repo\partly").Staged);
            Assert.False(snapshot.GetFile(@"C:\repo\new\F.cs").Staged);
            Assert.False(snapshot.GetDirectory(Root).Staged);
        }

        [Fact]
        public void Ignored_entries_mark_the_path_and_everything_under_ignored_folders()
        {
            var snapshot = StatusSnapshot.Parse(Root, Z("!! bin/", "!! src/appsettings.Local.json", " M src/App.cs"));

            Assert.True(snapshot.IsIgnored(@"C:\repo\bin"));
            Assert.True(snapshot.IsIgnored(@"C:\repo\bin\Debug\App.dll"));
            Assert.True(snapshot.IsIgnored(@"C:\repo\src\appsettings.Local.json"));
            Assert.False(snapshot.IsIgnored(@"C:\repo\src"));
            Assert.False(snapshot.IsIgnored(@"C:\repo\src\App.cs"));
            Assert.Null(snapshot.GetFile(@"C:\repo\bin"));
        }

        [Fact]
        public void Name_status_counts_copies_as_added_and_renames_as_modified()
        {
            var snapshot = StatusSnapshot.ParseNameStatus(Root, Z("M", "a.cs", "A", "dir/b.cs", "R087", "old.cs", "new.cs", "C075", "src.cs", "copy.cs", "D", "gone.cs"));

            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\a.cs"));
            Assert.Equal(GitStatus.Added, snapshot.GetFileStatus(@"C:\repo\dir\b.cs"));
            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\new.cs"));
            Assert.Equal(GitStatus.None, snapshot.GetFileStatus(@"C:\repo\old.cs"));
            Assert.Equal(GitStatus.Added, snapshot.GetFileStatus(@"C:\repo\copy.cs"));
            Assert.Equal(GitStatus.Modified, snapshot.GetFileStatus(@"C:\repo\gone.cs"));
            Assert.Equal(5, snapshot.FileCount);
        }

        [Fact]
        public void Truncated_output_does_not_throw()
        {
            var snapshot = StatusSnapshot.ParseNameStatus(Root, "R100\0old.cs\0");

            Assert.Equal(0, snapshot.FileCount);
        }

        [Fact]
        public void Same_content_detects_changes_in_counts_and_staging()
        {
            var a = StatusSnapshot.Parse(Root, Z(" M src/A.cs"));
            var b = StatusSnapshot.Parse(Root, Z(" M src/A.cs"));
            var staged = StatusSnapshot.Parse(Root, Z("M  src/A.cs"));
            var more = StatusSnapshot.Parse(Root, Z(" M src/A.cs", " M src/B.cs"));

            Assert.True(a.HasSameContent(b));
            Assert.False(a.HasSameContent(staged));
            Assert.False(a.HasSameContent(more));
            Assert.True(StatusSnapshot.Empty.HasSameContent(StatusSnapshot.Parse(Root, string.Empty)));
        }
    }
}
