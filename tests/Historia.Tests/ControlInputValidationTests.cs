namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class ControlInputValidationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ctl-root");

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("HEAD~3")]
    [InlineData("v1.2.0")]
    [InlineData("abc1234")]
    [InlineData("origin/feature/x")]
    public void Ordinary_revisions_are_accepted(string rev)
    {
        Assert.True(ControlInputValidation.TryValidateRevision(rev, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("-rf")]
    [InlineData("--output=x")]
    [InlineData("--all")]
    [InlineData("HEAD\n")]
    [InlineData("HE\0AD")]
    [InlineData("")]
    [InlineData(null)]
    public void Option_like_or_control_revisions_are_rejected(string? rev)
    {
        Assert.False(ControlInputValidation.TryValidateRevision(rev, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void An_overlong_revision_is_rejected()
    {
        Assert.False(ControlInputValidation.TryValidateRevision(new string('a', 300), out _));
        Assert.True(ControlInputValidation.TryValidateRevision(new string('a', 256), out _));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("sub/../../x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    public void Paths_that_escape_or_are_empty_are_rejected(string path)
    {
        Assert.False(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, path, out var relative));
        Assert.Equal(string.Empty, relative);
    }

    [Fact]
    public void An_absolute_path_outside_the_repo_is_rejected()
    {
        var other = Path.Combine(Path.GetTempPath(), "ctl-other", "x");

        Assert.False(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, other, out _));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_repo_name_prefix_is_rejected()
    {
        var sibling = Root + "-evil";

        Assert.False(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, Path.Combine(sibling, "a.txt"), out _));
    }

    [Fact]
    public void The_repo_root_itself_is_not_a_file()
    {
        Assert.False(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, ".", out _));
        Assert.False(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, Root, out _));
    }

    [Fact]
    public void A_relative_path_resolves_against_the_cwd_with_forward_slashes()
    {
        var sub = Path.Combine(Root, "sub");

        Assert.True(ControlInputValidation.TryResolveRepoRelativePath(Root, sub, Path.Combine("src", "a.cs"), out var relative));
        Assert.Equal("sub/src/a.cs", relative);
    }

    [Fact]
    public void A_backslash_path_from_a_subdirectory_resolves_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sub = Path.Combine(Root, "sub");

        Assert.True(ControlInputValidation.TryResolveRepoRelativePath(Root, sub, @"src\a.cs", out var relative));
        Assert.Equal("sub/src/a.cs", relative);
    }

    [Fact]
    public void A_deleted_file_that_is_not_on_disk_is_accepted()
    {
        Assert.True(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, "gone/deleted.txt", out var relative));
        Assert.Equal("gone/deleted.txt", relative);
    }

    [Fact]
    public void An_absolute_path_inside_the_repo_becomes_relative()
    {
        Assert.True(ControlInputValidation.TryResolveRepoRelativePath(Root, Path.GetTempPath(), Path.Combine(Root, "a", "b.txt"), out var relative));
        Assert.Equal("a/b.txt", relative);
    }

    [Fact]
    public void Path_case_is_ignored_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(ControlInputValidation.TryResolveRepoRelativePath(Root, Root, Path.Combine(Root.ToUpperInvariant(), "a.txt"), out var relative));
        Assert.Equal("a.txt", relative);
    }

    [Fact]
    public void A_message_has_crlf_and_trailing_blanks_normalized()
    {
        Assert.True(ControlInputValidation.NormalizeCommitMessage("feat: x  \r\n\r\nbody line \t\r\n\r\n", out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal("feat: x\n\nbody line", normalized);
    }

    [Fact]
    public void A_leading_bom_is_dropped()
    {
        Assert.True(ControlInputValidation.NormalizeCommitMessage("﻿feat: x", out var normalized, out _));
        Assert.Equal("feat: x", normalized);
    }

    [Fact]
    public void Tabs_and_a_multi_paragraph_body_survive()
    {
        const string message = "fix(core): thing\n\n- one\n\t- nested\n\nSecond paragraph.";

        Assert.True(ControlInputValidation.NormalizeCommitMessage(message, out var normalized, out _));
        Assert.Equal(message, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    [InlineData("bad\0nul")]
    [InlineData("bad\u001besc")]
    [InlineData("lone\rcr")]
    public void Empty_or_control_character_messages_are_rejected(string message)
    {
        Assert.False(ControlInputValidation.NormalizeCommitMessage(message, out var normalized, out var error));
        Assert.NotNull(error);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void A_null_message_is_rejected()
    {
        Assert.False(ControlInputValidation.NormalizeCommitMessage(null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_message_over_sixteen_kib_is_rejected_and_sixteen_kib_is_accepted()
    {
        Assert.False(ControlInputValidation.NormalizeCommitMessage(new string('a', 17 * 1024), out _, out _));
        Assert.True(ControlInputValidation.NormalizeCommitMessage(new string('a', 16 * 1024), out _, out _));
    }

    [Fact]
    public void The_size_limit_counts_utf8_bytes()
    {
        Assert.False(ControlInputValidation.NormalizeCommitMessage(new string('é', 9 * 1024), out _, out _));
    }

    [Theory]
    [InlineData("Review the parser", "Review the parser")]
    [InlineData("  padded  ", "padded")]
    [InlineData("two\nlines\r\nhere", "two lines here")]
    [InlineData("runs \t of   space", "runs of space")]
    public void A_note_is_trimmed_and_its_whitespace_collapsed(string text, string expected)
    {
        Assert.True(ControlInputValidation.NormalizeNote(text, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    [InlineData("bell\u0007")]
    [InlineData("esc\u001b[31m")]
    public void An_empty_or_control_note_is_rejected(string? text)
    {
        Assert.False(ControlInputValidation.NormalizeNote(text, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_note_is_at_most_two_hundred_characters_after_collapsing()
    {
        Assert.True(ControlInputValidation.NormalizeNote(new string('a', 200), out _, out _));
        Assert.False(ControlInputValidation.NormalizeNote(new string('a', 201), out _, out _));
        Assert.True(ControlInputValidation.NormalizeNote("  " + new string('a', 200) + "\n\n", out var trimmed, out _));
        Assert.Equal(200, trimmed!.Length);
    }

    [Fact]
    public void Existing_segments_take_their_on_disk_case_and_missing_ones_are_kept()
    {
        var root = TestScratch.NewDirectory("ctl-case-");
        Directory.CreateDirectory(Path.Combine(root, "Src", "Views"));
        File.WriteAllText(Path.Combine(root, "Src", "Views", "Foo.cs"), "");
        try
        {
            var foo = ControlInputValidation.MatchOnDiskCase(root, "src/views/foo.cs");
            var gone = ControlInputValidation.MatchOnDiskCase(root, "src/views/Gone.cs");

            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Assert.Equal("Src/Views/Foo.cs", foo);
                Assert.Equal("Src/Views/Gone.cs", gone);
            }
            else
            {
                Assert.Equal("src/views/foo.cs", foo);
                Assert.Equal("src/views/Gone.cs", gone);
            }
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }
}
