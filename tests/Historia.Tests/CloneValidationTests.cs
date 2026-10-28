namespace Historia.Tests;

public class CloneValidationTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("gh:owner/repo.git")]
    [InlineData("github.com:owner/repo.git")]
    [InlineData("my-host:repo")]
    public void DescribeUrlProblem_accepts_well_shaped_urls(string url)
    {
        Assert.Null(CloneValidation.DescribeUrlProblem(url));
    }

    [Fact]
    public void DescribeUrlProblem_accepts_an_existing_local_directory()
    {
        var dir = TestScratch.NewDirectory("historia-clone-validation-");
        try
        {
            Assert.Null(CloneValidation.DescribeUrlProblem(dir));
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some plain text")]
    [InlineData("-oProxyCommand=calc.exe")]
    [InlineData("-oProxyCommand=x:y")]
    [InlineData("ext::sh -c calc.exe")]
    [InlineData("ext::sh -c x")]
    [InlineData(@"C:\does-not-exist\repo")]
    [InlineData("C:repo")]
    public void DescribeUrlProblem_rejects_garbage_and_security_risks(string url)
    {
        Assert.NotNull(CloneValidation.DescribeUrlProblem(url));
    }

    [Fact]
    public void DescribeDestinationProblem_accepts_a_clean_new_folder()
    {
        var root = TestScratch.NewDirectory("historia-clone-validation-");
        try
        {
            Assert.Null(CloneValidation.DescribeDestinationProblem(root, $"new-{Guid.NewGuid():N}"));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void DescribeDestinationProblem_rejects_an_existing_non_empty_folder()
    {
        var root = TestScratch.NewDirectory("historia-clone-validation-");
        var folder = "existing";
        var target = Path.Combine(root, folder);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "hi");
        try
        {
            Assert.NotNull(CloneValidation.DescribeDestinationProblem(root, folder));
        }
        finally
        {
            TestScratch.Delete(root);
        }
    }

    [Fact]
    public void DescribeDestinationProblem_reports_an_unreadable_destination_instead_of_throwing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TestScratch.NewDirectory("historia-clone-unreadable-");
        var target = Path.Combine(root, "unreadable");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "data");

        var di = new DirectoryInfo(target);
        var sec = di.GetAccessControl();
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var denyRule = new System.Security.AccessControl.FileSystemAccessRule(
            currentUser,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);

        sec.AddAccessRule(denyRule);
        di.SetAccessControl(sec);

        try
        {
            var problem = CloneValidation.DescribeDestinationProblem(root, "unreadable");
            Assert.NotNull(problem);
            Assert.StartsWith("That destination can't be read:", problem);
        }
        finally
        {
            sec.RemoveAccessRule(denyRule);
            di.SetAccessControl(sec);
            TestScratch.Delete(root);
        }
    }

    [WindowsFact]
    public void DescribeDestinationProblem_rejects_a_segment_with_an_invalid_path_character()
    {
        // Verified live against .NET on Windows: '"' is one of Path.GetInvalidPathChars().
        Assert.NotNull(CloneValidation.DescribeDestinationProblem(@"C:\Projects", "\"Application\"}}]"));
    }

    [Theory]
    [InlineData("", "folder")]
    [InlineData("C:\\Projects", "")]
    public void DescribeDestinationProblem_rejects_empty_segments(string root, string folder)
    {
        Assert.NotNull(CloneValidation.DescribeDestinationProblem(root, folder));
    }
}
