namespace Historia.Tests;

public class GitStatusParserTests
{
    [Fact]
    public void Ordinary_entries_split_into_staged_and_unstaged()
    {
        var input = "1 A. N... 000000 100644 100644 0000000000000000000000000000000000000000 1111111111111111111111111111111111111111 staged.txt\01 .M N... 100644 100644 100644 2222222222222222222222222222222222222222 2222222222222222222222222222222222222222 unstaged.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var s = Assert.Single(parsed.Staged);
        Assert.Equal("staged.txt", s.Path);
        Assert.Equal(FileChangeKind.Added, s.Kind);

        var u = Assert.Single(parsed.Unstaged);
        Assert.Equal("unstaged.txt", u.Path);
        Assert.Equal(FileChangeKind.Modified, u.Kind);
        Assert.True(parsed.IsDirty);
    }

    [Fact]
    public void An_entry_changed_in_both_index_and_worktree_lands_in_both_lists()
    {
        var input = "1 MM N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 both.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var s = Assert.Single(parsed.Staged);
        Assert.Equal("both.txt", s.Path);
        Assert.Equal(FileChangeKind.Modified, s.Kind);

        var u = Assert.Single(parsed.Unstaged);
        Assert.Equal("both.txt", u.Path);
        Assert.Equal(FileChangeKind.Modified, u.Kind);
    }

    [Fact]
    public void A_staged_rename_carries_its_old_path()
    {
        var input = "2 R. N... 100644 100644 100644 1111111111111111111111111111111111111111 1111111111111111111111111111111111111111 R100 new.txt\0old.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var s = Assert.Single(parsed.Staged);
        Assert.Equal("new.txt", s.Path);
        Assert.Equal("old.txt", s.OldPath);
        Assert.Equal(FileChangeKind.Renamed, s.Kind);
    }

    [Fact]
    public void A_staged_copy_carries_its_old_path()
    {
        var input = "2 C. N... 100644 100644 100644 1111111111111111111111111111111111111111 1111111111111111111111111111111111111111 C100 copy.txt\0orig.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var s = Assert.Single(parsed.Staged);
        Assert.Equal("copy.txt", s.Path);
        Assert.Equal("orig.txt", s.OldPath);
        Assert.Equal(FileChangeKind.Copied, s.Kind);
    }

    [Fact]
    public void An_intent_to_add_rename_is_an_unstaged_rename()
    {
        var input = "2 .R N... 100644 100644 100644 1111111111111111111111111111111111111111 1111111111111111111111111111111111111111 R100 new.txt\0old.txt\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.Empty(parsed.Staged);
        var u = Assert.Single(parsed.Unstaged);
        Assert.Equal("new.txt", u.Path);
        Assert.Equal("old.txt", u.OldPath);
        Assert.Equal(FileChangeKind.Renamed, u.Kind);
    }

    [Fact]
    public void A_rename_staged_and_modified_in_worktree_lands_in_both_lists()
    {
        var input = "2 RM N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 R100 new.txt\0old.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var s = Assert.Single(parsed.Staged);
        Assert.Equal("new.txt", s.Path);
        Assert.Equal("old.txt", s.OldPath);
        Assert.Equal(FileChangeKind.Renamed, s.Kind);

        var u = Assert.Single(parsed.Unstaged);
        Assert.Equal("new.txt", u.Path);
        Assert.Null(u.OldPath);
        Assert.Equal(FileChangeKind.Modified, u.Kind);
    }

    [Fact]
    public void Unmerged_records_are_conflicted_only()
    {
        var input = "u UU N... 100644 100644 100644 100644 1111 2222 3333 conflict.txt\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.Empty(parsed.Staged);
        Assert.Empty(parsed.Unstaged);
        var c = Assert.Single(parsed.Conflicted);
        Assert.Equal("conflict.txt", c.Path);
        Assert.Equal(FileChangeKind.Conflicted, c.Kind);
    }

    [Fact]
    public void Untracked_paths_are_listed()
    {
        var input = "? untracked.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var u = Assert.Single(parsed.Untracked);
        Assert.Equal("untracked.txt", u.Path);
        Assert.Equal(FileChangeKind.Untracked, u.Kind);
    }

    [Fact]
    public void A_submodule_entry_is_flagged()
    {
        var input = "1 .M S... 160000 160000 160000 1111 1111 submod\0";
        var parsed = GitStatusParser.Parse(input);

        var u = Assert.Single(parsed.Unstaged);
        Assert.True(u.IsSubmodule);
    }

    [Fact]
    public void Branch_ab_header_sets_ahead_and_behind()
    {
        var input = "# branch.ab +3 -5\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.Equal(3, parsed.AheadBy);
        Assert.Equal(5, parsed.BehindBy);
    }

    [Fact]
    public void No_upstream_means_zero_ahead_and_behind()
    {
        var input = "# branch.head master\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.Equal(0, parsed.AheadBy);
        Assert.Equal(0, parsed.BehindBy);
    }

    [Fact]
    public void Paths_with_spaces_and_unicode_survive_z_parsing()
    {
        var input = "1 .M N... 100644 100644 100644 1111 1111 path with spaces and \u00e9t\u00e9.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var u = Assert.Single(parsed.Unstaged);
        Assert.Equal("path with spaces and \u00e9t\u00e9.txt", u.Path);
    }

    [Fact]
    public void Clean_output_is_not_dirty()
    {
        var input = "# branch.oid 1234\0# branch.head main\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.False(parsed.IsDirty);
    }

    [Fact]
    public void An_exact_hash_match_pairs_a_deletion_with_an_untracked_file()
    {
        var input = "1 .D N... 100644 100644 000000 1111111111111111111111111111111111111111 1111111111111111111111111111111111111111 old.txt\0? new.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var hashes = new Dictionary<string, string> { ["new.txt"] = "1111111111111111111111111111111111111111" };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Empty(status.Untracked);
        var u = Assert.Single(status.Unstaged);
        Assert.Equal("new.txt", u.Path);
        Assert.Equal("old.txt", u.OldPath);
        Assert.Equal(FileChangeKind.Renamed, u.Kind);
    }

    [Fact]
    public void Pairing_is_one_to_one_in_path_order()
    {
        var input = "1 .D N... 100644 100644 000000 1111 1111 a_old.txt\01 .D N... 100644 100644 000000 1111 1111 b_old.txt\0? a_new.txt\0? b_new.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var hashes = new Dictionary<string, string>
        {
            ["a_new.txt"] = "1111",
            ["b_new.txt"] = "1111",
        };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Empty(status.Untracked);
        Assert.Equal(2, status.Unstaged.Count);

        var first = Assert.Single(status.Unstaged, e => e.Path == "a_new.txt");
        Assert.Equal("a_old.txt", first.OldPath);
        Assert.Equal(FileChangeKind.Renamed, first.Kind);

        var second = Assert.Single(status.Unstaged, e => e.Path == "b_new.txt");
        Assert.Equal("b_old.txt", second.OldPath);
        Assert.Equal(FileChangeKind.Renamed, second.Kind);
    }

    [Fact]
    public void The_empty_blob_never_pairs()
    {
        var input = $"1 .D N... 100644 100644 000000 {GitStatusParser.EmptyBlobHash} {GitStatusParser.EmptyBlobHash} empty.txt\0? new_empty.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var hashes = new Dictionary<string, string>
        {
            ["new_empty.txt"] = GitStatusParser.EmptyBlobHash,
        };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Single(status.Unstaged, e => e.Path == "empty.txt" && e.Kind == FileChangeKind.Deleted);
        Assert.Single(status.Untracked, e => e.Path == "new_empty.txt");
    }

    [Fact]
    public void A_hash_mismatch_leaves_deleted_and_untracked()
    {
        var input = "1 .D N... 100644 100644 000000 1111 1111 old.txt\0? new.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var hashes = new Dictionary<string, string>
        {
            ["new.txt"] = "2222",
        };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Single(status.Unstaged, e => e.Path == "old.txt" && e.Kind == FileChangeKind.Deleted);
        Assert.Single(status.Untracked, e => e.Path == "new.txt");
    }

    [Fact]
    public void Hashing_is_skipped_without_a_worktree_deletion()
    {
        var input = "? loose.txt\0";
        var parsed = GitStatusParser.Parse(input);

        Assert.False(GitStatusParser.ShouldHashUntracked(parsed));

        // Also test non-regular file deletion (e.g. symlink 120000)
        var symlinkDel = "1 .D N... 120000 120000 000000 1111 1111 link.txt\0? loose.txt\0";
        var parsedSymlink = GitStatusParser.Parse(symlinkDel);
        Assert.False(GitStatusParser.ShouldHashUntracked(parsedSymlink));
    }

    [Fact]
    public void Hashing_is_skipped_above_1000_untracked_files()
    {
        var sb = new StringBuilder();
        sb.Append("1 .D N... 100644 100644 000000 1111 1111 old.txt\0");
        for (var i = 0; i < 1001; i++)
        {
            sb.Append($"? file{i}.txt\0");
        }

        var parsed = GitStatusParser.Parse(sb.ToString());
        Assert.Equal(1001, parsed.Untracked.Count);
        Assert.False(GitStatusParser.ShouldHashUntracked(parsed));
    }

    [Theory]
    [InlineData("­? weird.txt ")]
    [InlineData("​? weird.txt ")]
    [InlineData("­1 .M N... 100644 100644 100644 2222222222222222222222222222222222222222 2222222222222222222222222222222222222222 weird.txt ")]
    [InlineData("​# branch.ab +3 -5 ")]
    public void Record_prefixes_are_matched_ordinally(string input)
    {
        // Culture-sensitive comparison skips ignorable characters (soft hyphen, zero-width space),
        // so a culture-based prefix check would accept these records.
        var parsed = GitStatusParser.Parse(input);

        Assert.Empty(parsed.Staged);
        Assert.Empty(parsed.Unstaged);
        Assert.Empty(parsed.Untracked);
        Assert.Equal(0, parsed.AheadBy);
        Assert.Equal(0, parsed.BehindBy);
    }

    [Fact]
    public void A_large_payload_parses_completely()
    {
        var sb = new StringBuilder();
        sb.Append("# branch.oid 1111111111111111111111111111111111111111 # branch.head main # branch.ab +1 -2 ");
        for (var i = 0; i < 20_000; i++)
        {
            sb.Append((i % 3) switch
            {
                0 => $"1 .M N... 100644 100644 100644 2222222222222222222222222222222222222222 2222222222222222222222222222222222222222 src/dir{i % 50}/file{i}.cs ",
                1 => $"? build/out{i}/artifact{i}.bin ",
                _ => $"1 M. N... 100644 100644 100644 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 docs/page{i}.md ",
            });
        }

        var parsed = GitStatusParser.Parse(sb.ToString());

        Assert.Equal(6_667, parsed.Unstaged.Count);
        Assert.Equal(6_667, parsed.Untracked.Count);
        Assert.Equal(6_666, parsed.Staged.Count);
        Assert.Equal(1, parsed.AheadBy);
        Assert.Equal(2, parsed.BehindBy);
    }
}
