namespace Historia.Tests;

[Trait("tier", "unit")]
public class SidebarViewModelTests
{
    private static CmdrQueueItem Item(bool seen = false) => new()
    {
        RepoRoot = @"C:\repos\app",
        RepoName = "app",
        Target = ControlVerb.Open,
        Seen = seen,
    };

    [Fact]
    public void Branches_is_active_at_construction()
    {
        using var vm = new SidebarViewModel(new FakeCmdrQueueService());

        Assert.Equal(SidebarPane.Branches, vm.ActivePane);
        Assert.True(vm.IsBranchesActive);
        Assert.False(vm.IsCmdrActive);
    }

    [Fact]
    public void The_commands_switch_the_active_pane()
    {
        using var vm = new SidebarViewModel(new FakeCmdrQueueService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ShowCmdrCommand.Execute(null);
        Assert.Equal(SidebarPane.Cmdr, vm.ActivePane);
        Assert.True(vm.IsCmdrActive);
        Assert.False(vm.IsBranchesActive);
        Assert.Contains(nameof(SidebarViewModel.IsCmdrActive), changed);
        Assert.Contains(nameof(SidebarViewModel.IsBranchesActive), changed);

        vm.ShowBranchesCommand.Execute(null);
        Assert.Equal(SidebarPane.Branches, vm.ActivePane);
    }

    [Theory]
    [InlineData(0, "0", false)]
    [InlineData(7, "7", true)]
    [InlineData(99, "99", true)]
    [InlineData(120, "99+", true)]
    public void The_count_text_caps_at_99(int unseen, string expected, bool hasUnseen)
    {
        var queue = new FakeCmdrQueueService();
        for (var i = 0; i < unseen; i++)
            queue.Enqueue(Item());
        queue.Enqueue(Item(seen: true));

        using var vm = new SidebarViewModel(queue);

        Assert.Equal(unseen, vm.UnseenCount);
        Assert.Equal(expected, vm.CountText);
        Assert.Equal(hasUnseen, vm.HasUnseen);
    }

    [Fact]
    public void The_count_follows_the_queue()
    {
        var queue = new FakeCmdrQueueService();
        using var vm = new SidebarViewModel(queue);
        var first = Item();

        queue.Enqueue(first);
        queue.Enqueue(Item());
        Assert.Equal("2", vm.CountText);

        queue.MarkSeen(first.Id);
        Assert.Equal(1, vm.UnseenCount);
        Assert.True(vm.HasUnseen);
    }

    [Fact]
    public void Dispose_unsubscribes_from_the_queue()
    {
        var queue = new FakeCmdrQueueService();
        var vm = new SidebarViewModel(queue);
        Assert.Equal(1, queue.ChangedSubscriberCount);

        vm.Dispose();

        Assert.Equal(0, queue.ChangedSubscriberCount);
        queue.Enqueue(Item());
        Assert.Equal(0, vm.UnseenCount);
    }
}
