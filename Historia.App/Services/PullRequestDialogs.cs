namespace Historia.Services.UI;

/// <summary>The Create pull request window, shown through the existing dialog types.</summary>
public sealed class PullRequestDialogs(IExternalLauncher launcher) : IPullRequestDialogs
{
    public Task ShowAsync(CreatePullRequestRequest request) =>
        CreatePullRequestDialog.ShowAsync(request, launcher);
}
