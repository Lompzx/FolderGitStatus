using Xunit;

namespace FolderGitStatus.Tests
{
    public class ItemStatusTests
    {
        private static ItemStatus Pending(GitStatus kind) => new ItemStatus(kind, committed: false);

        private static ItemStatus Committed(GitStatus kind) => new ItemStatus(kind, committed: true);

        [Fact]
        public void Uncommitted_wins_over_committed_even_if_less_severe()
        {
            Assert.True(Pending(GitStatus.Added).IsMoreRelevantThan(Committed(GitStatus.Conflict)));
            Assert.False(Committed(GitStatus.Conflict).IsMoreRelevantThan(Pending(GitStatus.Added)));
        }

        [Fact]
        public void Within_a_group_the_more_severe_kind_wins()
        {
            Assert.True(Pending(GitStatus.Conflict).IsMoreRelevantThan(Pending(GitStatus.Modified)));
            Assert.True(Committed(GitStatus.Modified).IsMoreRelevantThan(Committed(GitStatus.Added)));
            Assert.False(Pending(GitStatus.Modified).IsMoreRelevantThan(Pending(GitStatus.Modified)));
        }

        [Fact]
        public void Anything_wins_over_none_and_none_never_wins()
        {
            Assert.True(Committed(GitStatus.Added).IsMoreRelevantThan(ItemStatus.None));
            Assert.False(ItemStatus.None.IsMoreRelevantThan(ItemStatus.None));
            Assert.False(ItemStatus.IgnoredItem.IsMoreRelevantThan(ItemStatus.None));
        }

        [Fact]
        public void Flags_only_apply_where_they_make_sense()
        {
            Assert.False(new ItemStatus(GitStatus.None, committed: true).Committed);
            Assert.False(new ItemStatus(GitStatus.Modified, committed: true, staged: true).Staged);
            Assert.False(new ItemStatus(GitStatus.Modified, committed: false, ignored: true).Ignored);
            Assert.True(ItemStatus.IgnoredItem.Ignored);
        }

        [Fact]
        public void Badge_count_comes_from_the_snapshot_the_status_was_taken_from()
        {
            var pending = StatusSnapshot.Parse(@"C:\r", " M a/1.cs\0 M a/2.cs\0").GetDirectory(@"C:\r\a");
            var branch = StatusSnapshot.ParseNameStatus(@"C:\r", "M\0a/1.cs\0M\0a/3.cs\0A\0a/4.cs\0").GetDirectory(@"C:\r\a");

            Assert.Equal(2, new ItemStatus(GitStatus.Modified, false, pending: pending, branch: branch).FileCount);
            Assert.Equal(3, new ItemStatus(GitStatus.Modified, true, branch: branch).FileCount);
            Assert.Equal(0, new ItemStatus(GitStatus.Modified, false, pending: pending).AsAggregate().FileCount);
        }

        [Fact]
        public void Tooltip_describes_pending_and_branch_changes()
        {
            var pending = StatusSnapshot.Parse(@"C:\r", "M  a/1.cs\0A  a/2.cs\0").GetDirectory(@"C:\r\a");
            var branch = StatusSnapshot.ParseNameStatus(@"C:\r", "M\0a/3.cs\0").GetDirectory(@"C:\r\a");
            var status = new ItemStatus(pending.Kind, false, staged: pending.Staged, pending: pending, branch: branch, baseRef: "origin/main");

            Assert.Equal("Uncommitted: 1 modified, 1 new (all staged)\nChanged on the branch (vs origin/main): 1 modified", status.Describe());
            Assert.Equal("Contains modified files", status.AsAggregate().Describe());
            Assert.Equal("Ignored by Git", ItemStatus.IgnoredItem.Describe());
            Assert.Null(ItemStatus.None.Describe());
        }

        [Fact]
        public void File_tooltip_names_the_kind()
        {
            var file = StatusSnapshot.Parse(@"C:\r", "M  a.cs\0").GetFile(@"C:\r\a.cs");

            Assert.Equal("Uncommitted: modified (staged)", new ItemStatus(file.Kind, false, staged: true, pending: file, isFile: true).Describe());
        }

        [Fact]
        public void Letters()
        {
            Assert.Equal("M", Pending(GitStatus.Modified).Letter);
            Assert.Equal("A", Pending(GitStatus.Added).Letter);
            Assert.Equal("C", Pending(GitStatus.Conflict).Letter);
            Assert.Null(ItemStatus.None.Letter);
        }
    }
}
