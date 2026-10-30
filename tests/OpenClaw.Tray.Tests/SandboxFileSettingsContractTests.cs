using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class SandboxFileSettingsContractTests
{
    [Fact]
    public void StorageAndBudgetRemainOutsideTheUnsupportedWindowsPermissionContainer()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var permissionContainer = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SandboxPermissionsControls");
        foreach (var name in new[] { "UploadStoragePath", "TransferBudgetBox" })
        {
            var control = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            Assert.DoesNotContain(permissionContainer, control.Ancestors());
        }
        var budget = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "TransferBudgetBox");
        Assert.Equal("1", (string?)budget.Attribute("Minimum"));
        Assert.Equal("1024", (string?)budget.Attribute("Maximum"));
        Assert.NotNull(budget.Attribute("AutomationProperties.LabeledBy"));
    }
}
