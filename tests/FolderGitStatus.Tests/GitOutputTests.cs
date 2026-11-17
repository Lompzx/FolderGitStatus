using System.Linq;
using Xunit;

namespace FolderGitStatus.Tests
{
    public class GitOutputTests
    {
        [Theory]
        [InlineData("origin/main", true)]
        [InlineData("develop", true)]
        [InlineData("release/2.0", true)]
        [InlineData("feature/ação", true)]
        [InlineData("", false)]
        [InlineData("-x", false)]
        [InlineData("a..b", false)]
        [InlineData("main; rm -rf", false)]
        [InlineData("main\"", false)]
        [InlineData("main/", false)]
        [InlineData("$(evil)", false)]
        public void Safe_ref_names(string name, bool expected)
        {
            Assert.Equal(expected, GitOutput.IsSafeRefName(name));
        }

        [Fact]
        public void Finds_the_rename_source_case_insensitively()
        {
            var output = "M\0a.cs\0R092\0src/Old Name.cs\0src/New.cs\0A\0b.cs\0";

            Assert.Equal("src/Old Name.cs", GitOutput.FindRenameSource(output, "src/new.cs"));
            Assert.Null(GitOutput.FindRenameSource(output, "a.cs"));
        }

        [Fact]
        public void Parses_the_ignored_folders_option()
        {
            Assert.Equal(new[] { @"\bin\", @"\obj\", @"\out\dist\" }, GitOutput.ParseIgnoredFolders(" bin; obj ;;/out/dist/;BIN"));
            Assert.Empty(GitOutput.ParseIgnoredFolders(""));
            Assert.Equal(4, GitOutput.ParseIgnoredFolders(GitOutput.DefaultIgnoredFolders).Length);
        }

        [Fact]
        public void Git_path_is_relative_with_forward_slashes()
        {
            Assert.Equal("src/App/Program.cs", GitOutput.ToGitPath(@"C:\repo", @"C:\repo\src\App\Program.cs"));
            Assert.Equal("a.cs", GitOutput.ToGitPath(@"C:\repo\", @"C:\repo\a.cs"));
        }

        [Fact]
        public void Quotes_arguments_with_spaces()
        {
            Assert.Equal("abc:src/a.cs", GitOutput.Quote("abc:src/a.cs"));
            Assert.Equal("\"abc:My Docs/a.cs\"", GitOutput.Quote("abc:My Docs/a.cs"));
        }

        [Fact]
        public void Actual_case_comes_from_the_disk()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FgsTest" + System.Guid.NewGuid().ToString("N").Substring(0, 6));
            var file = System.IO.Path.Combine(root, "Orders", "OrderService.cs");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            System.IO.File.WriteAllText(file, "x");
            try
            {
                var actual = PathUtil.GetActualCase(file.ToLowerInvariant());

                Assert.EndsWith(@"\Orders\OrderService.cs", actual);
                Assert.Equal(file, actual, ignoreCase: true);
                Assert.Equal(@"C:\nope\Missing.cs", PathUtil.GetActualCase(@"C:\nope\Missing.cs"), ignoreCase: true);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Name_status_entries_carry_the_source_path()
        {
            var changes = GitOutput.ParseNameStatus("C075\0a.cs\0b.cs\0D\0c.cs\0").ToList();

            Assert.Equal(2, changes.Count);
            Assert.Equal('C', changes[0].Letter);
            Assert.Equal("b.cs", changes[0].Path);
            Assert.Equal("a.cs", changes[0].SourcePath);
            Assert.Equal('D', changes[1].Letter);
            Assert.Equal("c.cs", changes[1].Path);
            Assert.Null(changes[1].SourcePath);
        }
    }
}
