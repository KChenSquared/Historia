namespace Historia.Services.Network;

/// <summary>The outcome of <see cref="PullRequestUrl.Build"/>: the link, or why none can be built.</summary>
public sealed record PullRequestUrlResult(string? Url, string? Problem);

/// <summary>
/// Builds the GitHub compare page URL that opens a pull request form. Pure string work: nothing is
/// fetched, and the result is only ever opened by an explicit user click.
/// </summary>
public static class PullRequestUrl
{
    private const string GitHubHost = "github.com";

    /// <summary>
    /// Builds <c>&lt;target web&gt;/compare/&lt;target branch&gt;...&lt;head&gt;?expand=1</c>. The head is
    /// <paramref name="headBranch"/> alone when both remotes are the same repository, otherwise
    /// <c>&lt;owner&gt;:&lt;branch&gt;</c> with the head repository's owner. Each branch path segment is
    /// escaped and the slashes between segments are kept.
    /// </summary>
    public static PullRequestUrlResult Build(string targetRemoteUrl, string targetBranch, string headRemoteUrl, string headBranch)
    {
        if (!TryGetGitHubWeb(targetRemoteUrl, out var targetWeb, out var problem)
            || !TryGetGitHubWeb(headRemoteUrl, out var headWeb, out problem))
            return new PullRequestUrlResult(null, problem);

        var head = SameRepository(targetWeb, headWeb)
            ? EscapePath(headBranch)
            : $"{Uri.EscapeDataString(OwnerOf(headWeb))}:{EscapePath(headBranch)}";

        return new PullRequestUrlResult(
            $"{targetWeb.TrimEnd('/')}/compare/{EscapePath(targetBranch)}...{head}?expand=1", null);
    }

    private static bool TryGetGitHubWeb(string remoteUrl, out string web, out string? problem)
    {
        web = "";
        problem = null;

        if (!RemoteWebUrl.TryGet(remoteUrl, out var webUrl))
        {
            problem = $"{remoteUrl} has no web address.";
            return false;
        }

        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            problem = "Pull request links are built for GitHub remotes only.";
            return false;
        }

        web = webUrl;
        return true;
    }

    private static bool SameRepository(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string OwnerOf(string webUrl)
    {
        var path = new Uri(webUrl).AbsolutePath.Trim('/');
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    private static string EscapePath(string branch) =>
        string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
}
