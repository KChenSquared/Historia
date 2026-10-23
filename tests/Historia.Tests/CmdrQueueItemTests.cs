namespace Historia.Tests;

[Trait("tier", "unit")]
public class CmdrQueueItemTests
{
    private const string Sha = "90236c7a1b2c3d4e5f60718293a4b5c6d7e8f901";

    [Fact]
    public void The_note_wins_over_every_fallback()
    {
        var item = new CmdrQueueItem
        {
            Target = ControlVerb.Show,
            Rev = Sha,
            CommitSubject = "feat: add the thing",
            Note = "Check the new menu builder",
        };

        Assert.Equal("Check the new menu builder", item.DisplayText);
    }

    [Fact]
    public void A_blank_note_falls_back()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Open, Note = "  " };

        Assert.Equal("Open repository", item.DisplayText);
    }

    [Theory]
    [InlineData(ControlVerb.Diff)]
    [InlineData(ControlVerb.Compare)]
    [InlineData(ControlVerb.Changes)]
    public void A_single_file_target_shows_the_file_name(ControlVerb target)
    {
        var item = new CmdrQueueItem { Target = target, Rev = Sha, Paths = ["src/Views/SidebarView.axaml"] };

        Assert.Equal("SidebarView.axaml", item.DisplayText);
    }

    [Fact]
    public void A_file_at_the_root_shows_its_whole_path()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Changes, Paths = ["README.md"] };

        Assert.Equal("README.md", item.DisplayText);
    }

    [Fact]
    public void Show_uses_the_commit_subject()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Show, Rev = Sha, CommitSubject = "fix(graph): keep status items" };

        Assert.Equal("fix(graph): keep status items", item.DisplayText);
    }

    [Fact]
    public void Show_without_a_subject_uses_the_short_sha()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Show, Rev = Sha };

        Assert.Equal("90236c7", item.DisplayText);
    }

    [Fact]
    public void Propose_commit_uses_the_first_line_of_the_message()
    {
        var item = new CmdrQueueItem
        {
            Target = ControlVerb.ProposeCommit,
            CommitMessage = "feat(cmdr): add the queue\r\n\r\nBody text that is not shown.",
        };

        Assert.Equal("feat(cmdr): add the queue", item.DisplayText);
    }

    [Fact]
    public void Several_files_show_a_count()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Changes, Paths = ["a.cs", "b/c.cs", "d.cs"] };

        Assert.Equal("3 files to review", item.DisplayText);
    }

    [Fact]
    public void Open_shows_open_repository()
    {
        var item = new CmdrQueueItem { Target = ControlVerb.Open };

        Assert.Equal("Open repository", item.DisplayText);
    }

    [Fact]
    public void Display_text_is_not_serialized()
    {
        var json = JsonSerializer.Serialize(new CmdrQueueItem { Target = ControlVerb.Open }, HistoriaJson.Options);

        Assert.DoesNotContain("displayText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_target_is_stored_in_the_protocol_kebab_form()
    {
        var json = JsonSerializer.Serialize(new CmdrQueueItem { Target = ControlVerb.ProposeCommit }, HistoriaJson.Options);

        Assert.Equal("propose-commit", JsonNode.Parse(json)!["target"]!.GetValue<string>());
        Assert.Equal(ControlVerb.ProposeCommit, JsonSerializer.Deserialize<CmdrQueueItem>(json, HistoriaJson.Options)!.Target);
    }

    [Fact]
    public void A_new_item_gets_a_unique_id_and_a_utc_time()
    {
        var a = new CmdrQueueItem();
        var b = new CmdrQueueItem();

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(32, a.Id.Length);
        Assert.Equal(TimeSpan.Zero, a.CreatedAt.Offset);
    }
}
