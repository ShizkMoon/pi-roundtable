using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeHostProcessTests
{
    [TestMethod]
    public void Command_fingerprint_is_stable_across_dictionary_order_and_changes_with_content()
    {
        var first = RuntimeHostProcess.CreateCommandFingerprint(
            "speech.broadcast",
            "user.direct_host",
            null,
            new Dictionary<string, object?>
            {
                ["message"] = "hello",
                ["mentions"] = new[] { "role.a" },
            });
        var same = RuntimeHostProcess.CreateCommandFingerprint(
            "speech.broadcast",
            "user.direct_host",
            null,
            new Dictionary<string, object?>
            {
                ["mentions"] = new[] { "role.a" },
                ["message"] = "hello",
            });
        var changed = RuntimeHostProcess.CreateCommandFingerprint(
            "speech.broadcast",
            "user.direct_host",
            null,
            new Dictionary<string, object?>
            {
                ["message"] = "different",
                ["mentions"] = new[] { "role.a" },
            });

        Assert.AreEqual(first, same);
        Assert.AreEqual(64, first.Length);
        Assert.AreNotEqual(first, changed);
    }

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

    [TestMethod]
    public async Task Durable_command_receipt_is_returned_after_runtime_process_restart_without_reexecution()
    {
        var script = FindRuntimeHostScript();
        Assert.IsTrue(File.Exists(script), "先运行 npm run build 生成 Runtime Host。 ");
        var previous = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        try
        {
            const string commandId = "durable-role-add";
            var firstStore = new MeetingEventStore(root);
            RuntimeCommandReceipt original;
            await using (var firstRuntime = new RuntimeHostProcess(firstStore))
            {
                await firstRuntime.StartAsync(ThreeRoleOptions(initialSequence: 0), CancellationToken.None);
                original = await firstRuntime.SendCommandAsync(
                    "role.add",
                    "role.architect",
                    null,
                    new Dictionary<string, object?>(),
                    CancellationToken.None,
                    commandId);
                Assert.IsTrue(original.Accepted, original.ErrorCode);
                await firstRuntime.StopAsync(RuntimeHostShutdownMode.Suspend, CancellationToken.None);
            }

            var replayedEvents = new List<RuntimeMeetingEvent>();
            await using (var restartedRuntime = new RuntimeHostProcess(new MeetingEventStore(root)))
            {
                restartedRuntime.MeetingEventReceived += (_, meetingEvent) => replayedEvents.Add(meetingEvent);
                await restartedRuntime.StartAsync(ThreeRoleOptions(initialSequence: 3), CancellationToken.None);
                var duplicate = await restartedRuntime.SendCommandAsync(
                    "role.add",
                    "role.architect",
                    null,
                    new Dictionary<string, object?>(),
                    CancellationToken.None,
                    commandId);
                Assert.AreEqual(original, duplicate);

                var conflict = await restartedRuntime.SendCommandAsync(
                    "role.add",
                    "role.other",
                    null,
                    new Dictionary<string, object?>(),
                    CancellationToken.None,
                    commandId);
                Assert.AreEqual("command_id_conflict", conflict.ErrorCode);
                Assert.IsFalse(conflict.Accepted);
                await restartedRuntime.StopAsync(RuntimeHostShutdownMode.Suspend, CancellationToken.None);
            }

            Assert.IsFalse(replayedEvents.Any(item => item.Kind == "role.registered"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Command_cancelled_after_durable_reservation_is_terminal_and_not_replayed()
    {
        var script = FindRuntimeHostScript();
        Assert.IsTrue(File.Exists(script), "先运行 npm run build 生成 Runtime Host。 ");
        var previous = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        try
        {
            const string commandId = "cancel-after-reserve";
            using var cancellation = new CancellationTokenSource();
            var journal = new CancellingCommandJournal(new MeetingEventStore(root), cancellation);
            var events = new List<RuntimeMeetingEvent>();
            await using var runtime = new RuntimeHostProcess(journal);
            runtime.MeetingEventReceived += (_, meetingEvent) => events.Add(meetingEvent);
            await runtime.StartAsync(ThreeRoleOptions(initialSequence: 0), CancellationToken.None);

            try
            {
                await runtime.SendCommandAsync(
                    "role.add",
                    "role.architect",
                    null,
                    new Dictionary<string, object?>(),
                    cancellation.Token,
                    commandId);
                Assert.Fail("The command should have been cancelled after durable reservation.");
            }
            catch (OperationCanceledException)
            {
                // TaskCanceledException is a valid concrete cancellation result.
            }

            var duplicate = await runtime.SendCommandAsync(
                "role.add",
                "role.architect",
                null,
                new Dictionary<string, object?>(),
                CancellationToken.None,
                commandId);
            Assert.AreEqual("command_outcome_unknown", duplicate.ErrorCode);
            Assert.IsFalse(duplicate.Accepted);
            Assert.IsFalse(events.Any(item => item.Kind == "role.registered"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("before_model_request", 0)]
    [DataRow("during_response", 1)]
    [DataRow("before_event_persist", 1)]
    [DataRow("after_event_persist", 1)]
    public async Task Strong_kill_cutpoints_never_repeat_a_side_effect(
        string cutpoint,
        int expectedSideEffects)
    {
        var script = FindCutpointFixture();
        Assert.IsTrue(File.Exists(script), "找不到 Runtime Host 强杀测试夹具。 ");
        var previousScript = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        var previousSideEffect = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE");
        var previousEventKind = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_TEST_EVENT_KIND");
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sideEffectFile = Path.Combine(root, "side-effects.txt");
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE", sideEffectFile);
        Environment.SetEnvironmentVariable(
            "PI_ROUNDTABLE_TEST_EVENT_KIND",
            cutpoint == "during_response" ? "speech.delta" : "message.published");
        try
        {
            const string commandId = "cutpoint-stable-command";
            var store = new MeetingEventStore(root);
            var blockingJournal = new BlockingCommandJournal(store);
            IMeetingEventStore journal = cutpoint == "before_model_request"
                ? blockingJournal
                : store;
            var targetEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using (var runtime = new RuntimeHostProcess(journal))
            {
                runtime.MeetingEventReceived += (_, meetingEvent) =>
                {
                    if (meetingEvent.Kind == "runtime.lease_acquired")
                    {
                        store.AppendAsync(meetingEvent).GetAwaiter().GetResult();
                        return;
                    }
                    if (meetingEvent.Kind is not ("message.published" or "speech.delta"))
                    {
                        return;
                    }
                    if (cutpoint is "during_response" or "after_event_persist")
                    {
                        store.AppendAsync(meetingEvent).GetAwaiter().GetResult();
                    }
                    runtime.Terminate();
                    targetEvent.TrySetResult();
                };
                await runtime.StartAsync(ThreeRoleOptions(initialSequence: 0), CancellationToken.None);

                var commandTask = runtime.SendCommandAsync(
                    "test.side_effect",
                    null,
                    null,
                    new Dictionary<string, object?> { ["value"] = "once" },
                    CancellationToken.None,
                    commandId);
                if (cutpoint == "before_model_request")
                {
                    await blockingJournal.ReservationReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    runtime.Terminate();
                    blockingJournal.ReleaseReservation();
                }
                else
                {
                    await targetEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await AssertCommandInterruptedAsync(commandTask);
            }

            Assert.AreEqual(expectedSideEffects, CountSideEffects(sideEffectFile));

            await using (var restarted = new RuntimeHostProcess(new MeetingEventStore(root)))
            {
                await restarted.StartAsync(ThreeRoleOptions(initialSequence: 100), CancellationToken.None);
                var duplicate = await restarted.SendCommandAsync(
                    "test.side_effect",
                    null,
                    null,
                    new Dictionary<string, object?> { ["value"] = "once" },
                    CancellationToken.None,
                    commandId);
                Assert.AreEqual("command_outcome_unknown", duplicate.ErrorCode);
                Assert.IsFalse(duplicate.Accepted);
                await restarted.StopAsync(RuntimeHostShutdownMode.Suspend, CancellationToken.None);
            }

            Assert.AreEqual(expectedSideEffects, CountSideEffects(sideEffectFile));
            Assert.IsTrue(expectedSideEffects is 0 or 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previousScript);
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE", previousSideEffect);
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_TEST_EVENT_KIND", previousEventKind);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Crash_recovery_advances_generation_preserves_private_audience_and_deduplicates_output()
    {
        var script = FindRecoveryFixture();
        Assert.IsTrue(File.Exists(script), "找不到 Runtime Host 恢复测试夹具。");
        var previousScript = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT");
        var previousSideEffect = Environment.GetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE");
        var root = Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sideEffectFile = Path.Combine(root, "recovery-side-effects.txt");
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", script);
        Environment.SetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE", sideEffectFile);
        try
        {
            const string stableCommandId = "recovery-private-stable";
            var firstStore = new MeetingEventStore(root);
            RuntimeCommandReceipt originalReceipt;
            await using (var firstRuntime = new RuntimeHostProcess(firstStore))
            {
                firstRuntime.MeetingEventReceived += (_, meetingEvent) =>
                    firstStore.AppendAsync(meetingEvent).GetAwaiter().GetResult();
                await firstRuntime.StartAsync(
                    ThreeRoleOptions(initialSequence: 0, runtimeGeneration: 1),
                    CancellationToken.None);
                originalReceipt = await firstRuntime.SendCommandAsync(
                    "speech.direct",
                    "user.direct_host",
                    "role.ux",
                    new Dictionary<string, object?> { ["message"] = "private recovery payload" },
                    CancellationToken.None,
                    stableCommandId);
                Assert.IsTrue(originalReceipt.Accepted, originalReceipt.ErrorCode);
                firstRuntime.Terminate();
            }

            var crashedCheckpoint = await firstStore.GetCheckpointAsync(
                "meeting-windows-three-role-test",
                CancellationToken.None);
            Assert.IsNotNull(crashedCheckpoint);
            Assert.AreEqual(2UL, crashedCheckpoint.LastSequence);
            Assert.AreEqual(1UL, crashedCheckpoint.RuntimeGeneration);
            Assert.IsFalse(crashedCheckpoint.CleanShutdown);
            Assert.AreEqual(1, CountSideEffects(sideEffectFile));

            var secondStore = new MeetingEventStore(root);
            await using (var recoveredRuntime = new RuntimeHostProcess(secondStore))
            {
                recoveredRuntime.MeetingEventReceived += (_, meetingEvent) =>
                    secondStore.AppendAsync(meetingEvent).GetAwaiter().GetResult();
                await recoveredRuntime.StartAsync(
                    ThreeRoleOptions(
                        initialSequence: crashedCheckpoint.LastSequence,
                        runtimeGeneration: 2),
                    CancellationToken.None);

                var duplicate = await recoveredRuntime.SendCommandAsync(
                    "speech.direct",
                    "user.direct_host",
                    "role.ux",
                    new Dictionary<string, object?> { ["message"] = "private recovery payload" },
                    CancellationToken.None,
                    stableCommandId);
                Assert.AreEqual(originalReceipt, duplicate);
                Assert.AreEqual(1, CountSideEffects(sideEffectFile));

                var publicReceipt = await recoveredRuntime.SendCommandAsync(
                    "test.public",
                    "user.direct_host",
                    null,
                    new Dictionary<string, object?> { ["message"] = "generation two output" },
                    CancellationToken.None,
                    "recovery-public-new");
                Assert.IsTrue(publicReceipt.Accepted, publicReceipt.ErrorCode);
                await recoveredRuntime.StopAsync(RuntimeHostShutdownMode.Suspend, CancellationToken.None);
            }

            var events = await secondStore.LoadEventsAsync(
                "meeting-windows-three-role-test",
                CancellationToken.None);
            CollectionAssert.AreEqual(
                new ulong[] { 1, 2, 3, 4, 5 },
                events.Select(meetingEvent => meetingEvent.Sequence).ToArray());
            CollectionAssert.AreEqual(
                new ulong[] { 1, 1, 2, 2, 2 },
                events.Select(meetingEvent => meetingEvent.RuntimeGeneration).ToArray());
            Assert.AreEqual(1, events.Count(meetingEvent => meetingEvent.Kind == "message.direct_sent"));
            var privateEvent = events.Single(meetingEvent => meetingEvent.Kind == "message.direct_sent");
            Assert.AreEqual("private", privateEvent.Visibility);
            CollectionAssert.AreEqual(
                new[] { "user.direct_host", "role.ux" },
                privateEvent.Audience.ToArray());
            Assert.IsFalse(events.Single(meetingEvent => meetingEvent.Kind == "message.published").Audience.Any());
            Assert.AreEqual(2, CountSideEffects(sideEffectFile));

            var recoveredCheckpoint = await secondStore.GetCheckpointAsync(
                "meeting-windows-three-role-test",
                CancellationToken.None);
            Assert.IsNotNull(recoveredCheckpoint);
            Assert.AreEqual(5UL, recoveredCheckpoint.LastSequence);
            Assert.AreEqual(2UL, recoveredCheckpoint.RuntimeGeneration);
            Assert.IsTrue(recoveredCheckpoint.CleanShutdown);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT", previousScript);
            Environment.SetEnvironmentVariable("PI_ROUNDTABLE_TEST_SIDE_EFFECT_FILE", previousSideEffect);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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

    private static RuntimeHostStartOptions ThreeRoleOptions(
        ulong initialSequence = 0,
        ulong runtimeGeneration = 1)
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
            runtimeGeneration,
            initialSequence,
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

    private static string FindCutpointFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "apps",
                "windows",
                "tests",
                "fixtures",
                "runtime-host-cutpoint.mjs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }

    private static string FindRecoveryFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "apps",
                "windows",
                "tests",
                "fixtures",
                "runtime-host-recovery.mjs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }

    private static int CountSideEffects(string path) => File.Exists(path)
        ? File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line))
        : 0;

    private static async Task AssertCommandInterruptedAsync(Task<RuntimeCommandReceipt> commandTask)
    {
        try
        {
            await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("The strong-killed command must not return a successful live receipt.");
        }
        catch (Exception error) when (
            error is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            // The durable journal is asserted after the process-level failure.
        }
    }

    private sealed class CancellingCommandJournal(
        IMeetingEventStore inner,
        CancellationTokenSource cancellation) : IMeetingEventStore
    {
        private int _cancelled;

        public string DatabasePath => inner.DatabasePath;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<bool> AppendAsync(
            RuntimeMeetingEvent meetingEvent,
            CancellationToken cancellationToken = default) =>
            inner.AppendAsync(meetingEvent, cancellationToken);

        public Task<IReadOnlyList<RuntimeMeetingEvent>> LoadEventsAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            inner.LoadEventsAsync(meetingId, cancellationToken);

        public Task<MeetingStoreCheckpoint?> GetCheckpointAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            inner.GetCheckpointAsync(meetingId, cancellationToken);

        public async Task<CommandJournalReservation> ReserveCommandAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default)
        {
            var reservation = await inner.ReserveCommandAsync(
                meetingId,
                commandId,
                fingerprint,
                cancellationToken);
            if (reservation.Disposition == CommandJournalReservationDisposition.Reserved &&
                Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                cancellation.Cancel();
            }
            return reservation;
        }

        public Task CompleteCommandAsync(
            string meetingId,
            string fingerprint,
            RuntimeCommandReceipt receipt,
            CancellationToken cancellationToken = default) =>
            inner.CompleteCommandAsync(meetingId, fingerprint, receipt, cancellationToken);

        public Task MarkCommandInterruptedAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default) =>
            inner.MarkCommandInterruptedAsync(meetingId, commandId, fingerprint, cancellationToken);
    }

    private sealed class BlockingCommandJournal(IMeetingEventStore inner) : IMeetingEventStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReservationReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DatabasePath => inner.DatabasePath;

        public void ReleaseReservation() => _release.TrySetResult();

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<bool> AppendAsync(
            RuntimeMeetingEvent meetingEvent,
            CancellationToken cancellationToken = default) =>
            inner.AppendAsync(meetingEvent, cancellationToken);

        public Task<IReadOnlyList<RuntimeMeetingEvent>> LoadEventsAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            inner.LoadEventsAsync(meetingId, cancellationToken);

        public Task<MeetingStoreCheckpoint?> GetCheckpointAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            inner.GetCheckpointAsync(meetingId, cancellationToken);

        public async Task<CommandJournalReservation> ReserveCommandAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default)
        {
            var reservation = await inner.ReserveCommandAsync(
                meetingId,
                commandId,
                fingerprint,
                cancellationToken);
            if (reservation.Disposition == CommandJournalReservationDisposition.Reserved)
            {
                ReservationReached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return reservation;
        }

        public Task CompleteCommandAsync(
            string meetingId,
            string fingerprint,
            RuntimeCommandReceipt receipt,
            CancellationToken cancellationToken = default) =>
            inner.CompleteCommandAsync(meetingId, fingerprint, receipt, cancellationToken);

        public Task MarkCommandInterruptedAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default) =>
            inner.MarkCommandInterruptedAsync(meetingId, commandId, fingerprint, cancellationToken);
    }
}
