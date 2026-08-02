using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class WorkspaceConfigurationStoreTests
{
    [TestMethod]
    public async Task Workspace_store_round_trips_reviewed_MCP_tools_and_exact_role_allowlists()
    {
        var root = TestRoot();
        try
        {
            var store = new WorkspaceConfigurationStore(root);
            var workspace = WorkspaceWithGrant("read_file");
            await store.SaveAsync(workspace);

            var loaded = await store.LoadAsync();
            Assert.HasCount(1, loaded.McpServers);
            Assert.HasCount(2, loaded.McpServers[0].ToolCatalog);
            Assert.HasCount(1, loaded.Roles[0].Capabilities.McpGrants);
            CollectionAssert.AreEqual(
                new[] { "read_file" },
                loaded.Roles[0].Capabilities.McpGrants[0].ToolAllowlist);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Workspace_store_rejects_a_role_tool_outside_the_reviewed_MCP_catalog()
    {
        var root = TestRoot();
        try
        {
            var store = new WorkspaceConfigurationStore(root);
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store.SaveAsync(WorkspaceWithGrant("delete_everything")));
            StringAssert.Contains(error.Message, "不在服务器复核目录");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Role_item_removes_server_and_tool_authority_together()
    {
        var role = new RoleItem("role.test", "Test", "long_term");
        role.SetMcpGrant("mcp.files", new[] { "read_file", "write_file" });
        Assert.AreEqual(2, role.McpToolCount);

        role.RemoveMcpGrant("mcp.files");
        Assert.DoesNotContain("mcp.files", role.McpServerIds);
        Assert.AreEqual(0, role.McpToolCount);
        Assert.IsEmpty(role.GetMcpToolAllowlist("mcp.files"));
    }

    [TestMethod]
    public void Frozen_participant_manifest_keeps_the_exact_role_MCP_allowlist()
    {
        var role = new RoleItem(
            "role.test",
            "Test",
            "long_term",
            "Test prompt",
            "model.test");
        role.FallbackModelProfileIds.Add("model.fallback");
        role.ThinkingLevel = "low";
        role.MaxOutputTokens = 320;
        role.SetMcpGrant("mcp.files", new[] { "write_file", "read_file" });

        var manifest = MainViewModel.BuildParticipantManifest(role);

        Assert.HasCount(1, manifest.CapabilitiesSnapshot.McpGrants);
        CollectionAssert.AreEqual(
            new[] { "read_file", "write_file" },
            manifest.CapabilitiesSnapshot.McpGrants[0].ToolAllowlist);
        Assert.AreEqual("model.test", manifest.ModelRouteSnapshot.PrimaryModelProfileId);
        CollectionAssert.AreEqual(
            new[] { "model.fallback" },
            manifest.ModelRouteSnapshot.FallbackModelProfileIds);
        Assert.AreEqual("low", manifest.ModelRouteSnapshot.ThinkingLevel);
        Assert.AreEqual(320, manifest.ModelRouteSnapshot.MaxOutputTokens);
    }

    private static WorkspaceConfiguration WorkspaceWithGrant(string toolName) => new()
    {
        WorkspaceId = "workspace.test",
        DisplayName = "Test",
        McpServers =
        [
            new McpServerProfileConfiguration
            {
                McpServerId = "mcp.files",
                DisplayName = "Files",
                Transport = "stdio",
                Command = "node",
                ToolCatalog =
                [
                    new McpToolProfileConfiguration { Name = "read_file", DisplayName = "Read file" },
                    new McpToolProfileConfiguration { Name = "write_file", DisplayName = "Write file" },
                ],
            },
        ],
        Roles =
        [
            new RoleProfileConfiguration
            {
                RoleProfileId = "role.test",
                DisplayName = "Test role",
                Description = "Test",
                SystemPrompt = "Test prompt",
                Responsibilities = ["Test"],
                Capabilities = new CapabilityPolicyConfiguration
                {
                    McpGrants =
                    [
                        new McpGrantConfiguration
                        {
                            McpServerId = "mcp.files",
                            ToolAllowlist = [toolName],
                            ApprovalMode = "always",
                            ExecutionMode = "direct",
                        },
                    ],
                },
            },
        ],
    };

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
