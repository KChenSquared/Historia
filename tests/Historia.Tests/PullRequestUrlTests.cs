namespace Historia.Tests;

[Trait("tier", "unit")]
public class PullRequestUrlTests
{
    private const string Origin = "https://github.com/acme/widgets.git";

    [Fact]
    public void Same_repo_https_remote()
    {
        var r = PullRequestUrl.Build(Origin, "main", Origin, "feature");

        Assert.Null(r.Problem);
        Assert.Equal("https://github.com/acme/widgets/compare/main...feature?expand=1", r.Url);
    }

    [Fact]
    public void Ssh_remote_maps_to_web_url()
    {
        var r = PullRequestUrl.Build("git@github.com:acme/widgets.git", "main", "git@github.com:acme/widgets.git", "feature");

        Assert.Null(r.Problem);
        Assert.Equal("https://github.com/acme/widgets/compare/main...feature?expand=1", r.Url);
    }

    [Fact]
    public void Fork_uses_owner_prefix()
    {
        var r = PullRequestUrl.Build(Origin, "main", "git@github.com:kevin/widgets.git", "feature");

        Assert.Null(r.Problem);
        Assert.Equal("https://github.com/acme/widgets/compare/main...kevin:feature?expand=1", r.Url);
    }

    [Fact]
    public void Same_repo_ignores_case_and_trailing_slash()
    {
        var r = PullRequestUrl.Build(Origin, "main", "https://GitHub.com/Acme/Widgets/", "feature");

        Assert.Equal("https://github.com/acme/widgets/compare/main...feature?expand=1", r.Url);
    }

    [Fact]
    public void Branch_with_slashes_and_hash_is_escaped_per_segment()
    {
        var r = PullRequestUrl.Build(Origin, "release/1.0", Origin, "fix/issue#12 x");

        Assert.Equal(
            "https://github.com/acme/widgets/compare/release/1.0...fix/issue%2312%20x?expand=1", r.Url);
    }

    [Fact]
    public void Non_github_host_reports_problem()
    {
        var r = PullRequestUrl.Build("https://gitlab.com/acme/widgets.git", "main", "https://gitlab.com/acme/widgets.git", "f");

        Assert.Null(r.Url);
        Assert.Equal("Pull request links are built for GitHub remotes only.", r.Problem);
    }

    [Fact]
    public void Non_github_head_reports_problem()
    {
        var r = PullRequestUrl.Build(Origin, "main", "https://gitlab.com/kevin/widgets.git", "f");

        Assert.Null(r.Url);
        Assert.Equal("Pull request links are built for GitHub remotes only.", r.Problem);
    }

    [Fact]
    public void Local_path_remote_reports_problem()
    {
        var r = PullRequestUrl.Build(@"C:\repos\origin", "main", @"C:\repos\origin", "f");

        Assert.Null(r.Url);
        Assert.Equal(@"C:\repos\origin has no web address.", r.Problem);
    }
}
