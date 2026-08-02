using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class RuntimeHostProcessTests
{
    [TestMethod]
    public async Task Windows_supervisor_delivers_three_role_registration_events_in_order()
    {
        var script = FindRuntimeHostScript();
        Assert.IsTrue(File.Exists(script), "先运行 npm run build 生成 Runtime Host。 ");
        var previous = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        try
        {
            await using var runtime = new RuntimeHostProcess();
            var events = new List<RuntimeMeetingEvent>();
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.MeetingEventReceived += (_, meetingEvent) =>
            {
                lock (events)
                {
                    events.Add(meetingEvent);
                    if (meetingEvent.Kind == "meeting.opened")
                    {
                        opened.TrySetResult();
                    }
                }
            };

            await runtime.StartAsync(ThreeRoleOptions(), CancellationToken.None);
            foreach (var roleId in new[] { "role.architect", "role.ux", "role.critic" })
            {
                var receipt = await runtime.SendCommandAsync(
                    "role.add",
                    roleId,
                    null,
                    new Dictionary<string, object?>(),
                    CancellationToken.None);
                Assert.IsTrue(receipt.Accepted, receipt.ErrorCode);
            }
            var openReceipt = await runtime.SendCommandAsync(
                "meeting.open",
                null,
                null,
                new Dictionary<string, object?>(),
                CancellationToken.None);
            Assert.IsTrue(openReceipt.Accepted, openReceipt.ErrorCode);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

            RuntimeMeetingEvent[] snapshot;
            lock (events)
            {
                snapshot = events.Take(5).ToArray();
            }
            Assert.AreEqual(
                "1:runtime.lease_acquired,2:role.registered,3:role.registered,4:role.registered,5:meeting.opened",
                string.Join(',', snapshot.Select(item => $"{item.Sequence}:{item.Kind}")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previous);
        }
    }

    [TestMethod]
    public async Task Windows_supervisor_negotiates_v3_and_suspends_without_closing()
    {
        var script = FindRuntimeHostScript();
        Assert.IsTrue(File.Exists(script), "先运行 npm run build 生成 Runtime Host。 ");
        var previous = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        try
        {
            await using var runtime = new RuntimeHostProcess();
            var events = new List<RuntimeMeetingEvent>();
            var receivedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.MeetingEventReceived += (_, meetingEvent) =>
            {
                lock (events)
                {
                    events.Add(meetingEvent);
                    if (meetingEvent.Kind == "runtime.lease_released")
                    {
                        receivedRelease.TrySetResult();
                    }
                }
            };

            await runtime.StartAsync(Options(initialSequence: 5), CancellationToken.None);
            await runtime.StopAsync(RuntimeHostShutdownMode.Suspend, CancellationToken.None);
            await receivedRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));

            RuntimeMeetingEvent[] snapshot;
            lock (events)
            {
                snapshot = events.ToArray();
            }
            Assert.AreEqual(
                "runtime.lease_acquired,runtime.lease_released",
                string.Join(',', snapshot.Select(meetingEvent => meetingEvent.Kind)));
            Assert.AreEqual(6UL, snapshot[0].Sequence);
            Assert.AreEqual(7UL, snapshot[1].Sequence);
            Assert.IsFalse(snapshot.Any(meetingEvent => meetingEvent.Kind == "meeting.closed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previous);
        }
    }

    private static RuntimeHostStartOptions Options(ulong initialSequence)
    {
        var workspace = new WorkspaceConfiguration
        {
            WorkspaceId = "workspace.windows-supervisor-test",
            DisplayName = "Windows supervisor test",
            Providers =
            [
                new ProviderProfileConfiguration
                {
                    ProviderProfileId = "provider.test",
                    DisplayName = "Test provider",
                    ApiFamily = "custom",
                    RuntimeProviderId = "test",
                    CredentialRef = "memory://provider.test",
                },
            ],
            Models =
            [
                new ModelProfileConfiguration
                {
                    ModelProfileId = "model.test",
                    ProviderProfileId = "provider.test",
                    ModelId = "test-model",
                    DisplayName = "Test model",
                    Capabilities = ["text"],
                },
            ],
            Roles =
            [
                new RoleProfileConfiguration
                {
                    RoleProfileId = "role.test",
                    DisplayName = "Test role",
                    Description = "Protocol-only role",
                    SystemPrompt = "Do not start during this draft protocol test.",
                    Responsibilities = ["Protocol test"],
                    ModelRoute = new ModelRouteConfiguration { PrimaryModelProfileId = "model.test" },
                },
            ],
        };
        var session = new RoundtableSessionConfiguration
        {
            SessionId = "meeting-windows-supervisor-test",
            WorkspaceId = workspace.WorkspaceId,
            Title = "Windows supervisor test",
            Phase = "draft",
            Agenda = new SessionAgendaConfiguration { Subject = "Protocol test" },
            Participants =
            [
                new ParticipantManifestConfiguration
                {
                    ParticipantId = "role.test",
                    Scope = "long_term",
                    RoleProfileId = "role.test",
                    DisplayName = "Test role",
                    SystemPromptSnapshot = "Do not start during this draft protocol test.",
                    ModelRouteSnapshot = new ModelRouteConfiguration { PrimaryModelProfileId = "model.test" },
                    RetentionPolicy = "retain_profile",
                },
            ],
        };
        return new RuntimeHostStartOptions(
            session.SessionId,
            "runtime-windows-supervisor-test",
            1,
            initialSequence,
            workspace,
            session,
            new Dictionary<string, string> { ["memory://provider.test"] = "test-only" });
    }

    private static RuntimeHostStartOptions ThreeRoleOptions()
    {
        var workspace = new WorkspaceConfiguration
        {
            WorkspaceId = "workspace.windows-three-role-test",
            DisplayName = "Windows three-role test",
            Providers =
            [
                new ProviderProfileConfiguration
                {
                    ProviderProfileId = "provider.deepseek.test",
                    DisplayName = "DeepSeek",
                    ApiFamily = "openai_chat_completions",
                    RuntimeProviderId = "deepseek",
                    Endpoint = "https://api.deepseek.com",
                    CredentialRef = "memory://provider.deepseek.test",
                },
            ],
            Models =
            [
                new ModelProfileConfiguration
                {
                    ModelProfileId = "model.deepseek.test",
                    ProviderProfileId = "provider.deepseek.test",
                    ModelId = "deepseek-chat",
                    DisplayName = "deepseek-chat",
                    Capabilities = ["text"],
                    ContextWindow = 128_000,
                },
            ],
        };
        var displayNames = new Dictionary<string, string>
        {
            ["role.architect"] = "体系架构师",
            ["role.ux"] = "产品体验官",
            ["role.critic"] = "风险审查员",
        };
        foreach (var roleId in new[] { "role.architect", "role.ux", "role.critic" })
        {
            workspace.Roles.Add(new RoleProfileConfiguration
            {
                RoleProfileId = roleId,
                DisplayName = displayNames[roleId],
                Description = "Protocol integration role",
                SystemPrompt = "Do not call the provider during this startup test.",
                Responsibilities = ["Protocol test"],
                ModelRoute = new ModelRouteConfiguration { PrimaryModelProfileId = "model.deepseek.test" },
                Delegation = new DelegationPolicyConfiguration
                {
                    NetworkAccess = "forbidden",
                    ResultMode = "summary",
                    MaxConcurrentSubagents = 0,
                },
                Memory = new MemoryPolicyConfiguration
                {
                    Mode = "disabled",
                    WriteApproval = "always",
                    PromptEvolution = "disabled",
                },
            });
        }
        var session = new RoundtableSessionConfiguration
        {
            SessionId = "meeting-windows-three-role-test",
            WorkspaceId = workspace.WorkspaceId,
            Title = "Windows three-role test",
            Phase = "draft",
            Agenda = new SessionAgendaConfiguration { Subject = "Protocol event delivery" },
            Participants = workspace.Roles.Select(role => new ParticipantManifestConfiguration
            {
                ParticipantId = role.RoleProfileId,
                Scope = "long_term",
                RoleProfileId = role.RoleProfileId,
                DisplayName = role.DisplayName,
                SystemPromptSnapshot = role.SystemPrompt,
                ModelRouteSnapshot = role.ModelRoute,
                CapabilitiesSnapshot = role.Capabilities,
                DelegationSnapshot = role.Delegation,
                MemoryPolicySnapshot = role.Memory,
                RetentionPolicy = "retain_profile",
            }).ToList(),
        };
        return new RuntimeHostStartOptions(
            session.SessionId,
            "runtime-windows-three-role-test",
            1,
            0,
            workspace,
            session,
            new Dictionary<string, string> { ["memory://provider.deepseek.test"] = "test-only" });
    }

    private static string FindRuntimeHostScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "packages",
                "runtime-host",
                "dist",
                "host-main.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }
}
