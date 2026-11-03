namespace HistoriaApp.Tests;

/// <summary>
/// A text prompt (New branch, New tag, Rename, ...) must put the caret in its text box as soon as
/// it opens, and select a prefilled value so typing replaces it.
/// </summary>
[Trait("tier", "ui")]
public class PromptDialogFocusTests
{
    private static async Task<(Window Owner, Window Dialog, Task<SimpleDialog.Result> Pending)> OpenPromptAsync(string? defaultValue)
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        var pending = SimpleDialog.ShowAsync(
            owner, "Prompt", "Name?", prompt: true, "OK", "Cancel", defaultValue);

        for (var i = 0; i < 100 && owner.OwnedWindows.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.NotEmpty(owner.OwnedWindows);
        var dialog = owner.OwnedWindows[0];

        for (var i = 0; i < 10; i++)
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        return (owner, dialog, pending);
    }

    private static async Task CloseAsync(Window owner, Window dialog, Task<SimpleDialog.Result> pending)
    {
        dialog.Close();
        await pending;
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Prompt_FocusesTextBoxOnOpen()
    {
        var (owner, dialog, pending) = await OpenPromptAsync(defaultValue: null);
        try
        {
            var box = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Same(box, dialog.FocusManager?.GetFocusedElement());
        }
        finally
        {
            await CloseAsync(owner, dialog, pending);
        }
    }

    [AvaloniaFact]
    public async Task Prompt_SelectsPrefilledText()
    {
        var (owner, dialog, pending) = await OpenPromptAsync(defaultValue: "main");
        try
        {
            var box = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal(4, box.SelectionEnd);
        }
        finally
        {
            await CloseAsync(owner, dialog, pending);
        }
    }
}
