using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Adapters.SystemAccess;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

public sealed class ComInvokeRuntimeCoverageTests
{
    [Fact]
    public async Task ArgumentBinding_Covers_Optional_Named_Overload_ByRef_Enum_And_SpecialConverters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.optional",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "EchoOptional", ComCoverageDsl.Arg("prefix", "string"))]),
            ComCoverageDsl.Command(
                "det.named",
                "det",
                "application",
                [ComCoverageDsl.Step(
                    "call",
                    "NamedOptional",
                    ComCoverageDsl.Arg("third", "string", name: "third"),
                    ComCoverageDsl.Arg("first", "string", name: "first"))]),
            ComCoverageDsl.Command(
                "det.overload.int",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "Overload", ComCoverageDsl.Arg("value", "int"))]),
            ComCoverageDsl.Command(
                "det.overload.string",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "Overload", ComCoverageDsl.Arg("value", "string"))]),
            ComCoverageDsl.Command(
                "det.describe.dbnull",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "DescribeValue", ComCoverageDsl.Literal(null, "dbnull"))]),
            ComCoverageDsl.Command(
                "det.describe.null",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "DescribeValue", ComCoverageDsl.Literal(null, "null"))]),
            ComCoverageDsl.Command(
                "det.describe.matrix",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "DescribeValue", ComCoverageDsl.Arg("matrix", "matrix"))]),
            ComCoverageDsl.Command(
                "det.enum",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "AcceptState", ComCoverageDsl.Arg("state", "enum"))],
                enumMap: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ready"] = nameof(DeterministicState.Ready),
                }),
            ComCoverageDsl.Command(
                "det.byref",
                "det",
                "application",
                [ComCoverageDsl.Step(
                    "call",
                    "Mutate",
                    ComCoverageDsl.Arg("value", "int", byRef: true, captureAs: "updated"),
                    ComCoverageDsl.Literal(null, name: "status", byRef: true, captureAs: "status"))],
                returnPath: "stored:status"));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, ComCoverageFixture.CreateDescriptor());

        CommandExecutionResult optional = await ExecuteAsync(harness, "det.optional", new JsonObject { ["prefix"] = "alpha" });
        CommandExecutionResult named = await ExecuteAsync(harness, "det.named", new JsonObject { ["first"] = "one", ["third"] = "three" });
        CommandExecutionResult overloadInt = await ExecuteAsync(harness, "det.overload.int", new JsonObject { ["value"] = 42 });
        CommandExecutionResult overloadString = await ExecuteAsync(harness, "det.overload.string", new JsonObject { ["value"] = "forty-two" });
        CommandExecutionResult dbnull = await ExecuteAsync(harness, "det.describe.dbnull");
        CommandExecutionResult nullValue = await ExecuteAsync(harness, "det.describe.null");
        CommandExecutionResult matrix = await ExecuteAsync(
            harness,
            "det.describe.matrix",
            new JsonObject
            {
                ["matrix"] = new JsonArray
                {
                    new JsonArray { 1, 2 },
                    new JsonArray { 3, 4 },
                },
            });
        CommandExecutionResult enumResult = await ExecuteAsync(harness, "det.enum", new JsonObject { ["state"] = "ready" });
        CommandExecutionResult byRef = await ExecuteAsync(harness, "det.byref", new JsonObject { ["value"] = 5 });

        Assert.Equal("alpha:default", optional.Result?.GetValue<string>());
        Assert.Equal("one|two|three", named.Result?.GetValue<string>());
        Assert.Equal("int:42", overloadInt.Result?.GetValue<string>());
        Assert.Equal("string:forty-two", overloadString.Result?.GetValue<string>());
        Assert.Equal("dbnull", dbnull.Result?.GetValue<string>());
        Assert.Equal("null", nullValue.Result?.GetValue<string>());
        Assert.Equal("matrix:2x2:1", matrix.Result?.GetValue<string>());
        Assert.Equal("state:Ready", enumResult.Result?.GetValue<string>());
        Assert.Equal("status:12", byRef.Result?.GetValue<string>());
        Assert.Equal(12, byRef.Report?["storedValues"]?["updated"]?.GetValue<int>());
    }

    [Fact]
    public async Task Surface_Metadata_And_Cast_Model_Are_Reported_For_MultiInterface_Handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.view",
                "det",
                "application",
                [
                    ComCoverageDsl.Step("get", "ActiveDocument"),
                    ComCoverageDsl.Step("get", "ActiveView"),
                ]),
            ComCoverageDsl.Command(
                "det.view.cast.symbols",
                "det",
                "handle",
                [ComCoverageDsl.Step("cast", "symbols")]),
            ComCoverageDsl.Command(
                "det.view.query.symbols",
                "det",
                "handle",
                [ComCoverageDsl.Step("queryInterface", ComCoverageFixture.SymbolsSurfaceIid)]),
            ComCoverageDsl.Command(
                "det.view.try.table",
                "det",
                "handle",
                [ComCoverageDsl.Step("tryCast", "table")]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, ComCoverageFixture.CreateDescriptor());

        CommandExecutionResult view = await ExecuteAsync(harness, "det.view");
        JsonObject viewNode = AssertResultObject(view);
        string handleId = viewNode["handleId"]!.GetValue<string>();

        CommandExecutionResult cast = await ExecuteAsync(harness, "det.view.cast.symbols", new JsonObject { ["handleId"] = handleId });
        JsonObject castNode = AssertResultObject(cast);
        CommandExecutionResult query = await ExecuteAsync(harness, "det.view.query.symbols", new JsonObject { ["handleId"] = handleId });
        JsonObject queryNode = AssertResultObject(query);
        CommandExecutionResult tryTable = await ExecuteAsync(harness, "det.view.try.table", new JsonObject { ["handleId"] = handleId });

        Assert.Equal("IComCoverageView", viewNode["surface"]?.GetValue<string>());
        Assert.Contains("IComCoverageSymbols", viewNode["possibleCasts"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("IComCoverageSymbols", castNode["surface"]?.GetValue<string>());
        Assert.Contains("IComCoverageView", castNode["resolvedInterfaces"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("IComCoverageSymbols", castNode["resolvedInterfaces"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("DrawingTables", castNode["memberNames"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("IComCoverageSymbols", queryNode["surface"]?.GetValue<string>());
        Assert.Null(tryTable.Result);
    }

    [Fact]
    public async Task Ambiguous_Member_Diagnostic_Suggests_Cast_Hint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.view.bad-member",
                "det",
                "application",
                [
                    ComCoverageDsl.Step("get", "ActiveDocument"),
                    ComCoverageDsl.Step("get", "ActiveView"),
                    ComCoverageDsl.Step("get", "DrawingTables"),
                ]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, ComCoverageFixture.CreateDescriptor());

        CommandExecutionResult result = await ExecuteAsync(harness, "det.view.bad-member", expectSuccess: false);

        Assert.False(result.Success);
        Assert.Equal("det_invoke_failed", result.Error?.Code);
        Assert.Contains("Try cast(IComCoverageSymbols)", result.Error?.Message);
        Assert.Contains("Name", result.Error?.Message);
    }

    [Fact]
    public async Task SharedContext_Resolves_Last_And_Named_Handles_And_Reuses_Application()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor(configure: static value =>
        {
            value.ResultHints.Add(new ComResultHintDefinition
            {
                Name = "document",
                RequiredAllMembers = ["Title"],
                Fields =
                [
                    new ComResultFieldDefinition
                    {
                        Name = "title",
                        MemberPaths = ["Title"],
                    },
                ],
            });
        });
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.document",
                "det",
                "application",
                [ComCoverageDsl.Step("get", "ActiveDocument")]),
            ComCoverageDsl.Command(
                "det.document.title",
                "det",
                "handle",
                [ComCoverageDsl.Step("get", "Title")]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, descriptor);
        string sharedContextId = "ctx-shared";

        CommandExecutionResult first = await ExecuteAsync(
            harness,
            "det.document",
            sharedContextId: sharedContextId);
        CommandExecutionResult named = await ExecuteAsync(
            harness,
            "det.document.title",
            new JsonObject { ["contextHandle"] = "document" },
            sharedContextId: sharedContextId);
        CommandExecutionResult last = await ExecuteAsync(
            harness,
            "det.document.title",
            sharedContextId: sharedContextId);
        CommandExecutionResult second = await ExecuteAsync(
            harness,
            "det.document",
            sharedContextId: sharedContextId);

        string title = AssertResultObject(first)["title"]!.GetValue<string>();
        Assert.Equal(title, named.Result?.GetValue<string>());
        Assert.Equal(title, last.Result?.GetValue<string>());
        Assert.Equal(title, AssertResultObject(second)["title"]!.GetValue<string>());
        Assert.Equal(1, provider.GetAcquireCount(ComCoverageFixture.DefaultProgId));
    }

    [Fact]
    public async Task Refresh_Replaces_Cached_Application_And_Old_Handle_Becomes_Stale()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command("det.application", "det", "application", []),
            ComCoverageDsl.Command(
                "det.application.capture",
                "det",
                "handle",
                [ComCoverageDsl.Step("call", "CaptureThread", ComCoverageDsl.Arg("tag", "string"))]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, ComCoverageFixture.CreateDescriptor());
        string sharedContextId = "ctx-refresh";

        CommandExecutionResult first = await ExecuteAsync(harness, "det.application", sharedContextId: sharedContextId);
        string staleHandleId = AssertResultObject(first)["handleId"]!.GetValue<string>();
        DeterministicComApplication originalApplication = provider.EnsureRunning();

        DeterministicComApplication replacement = provider.ReplaceRunning();
        Assert.NotSame(originalApplication, replacement);

        CommandExecutionResult refreshed = await ExecuteAsync(
            harness,
            "det.application",
            new JsonObject { ["refresh"] = true },
            sharedContextId: sharedContextId);
        string freshHandleId = AssertResultObject(refreshed)["handleId"]!.GetValue<string>();
        CommandExecutionResult fresh = await ExecuteAsync(
            harness,
            "det.application.capture",
            new JsonObject
            {
                ["handleId"] = freshHandleId,
                ["tag"] = "fresh",
            });

        Assert.NotEqual(staleHandleId, freshHandleId);
        Assert.StartsWith("fresh:", fresh.Result?.GetValue<string>());
        Assert.Equal(2, provider.GetAcquireCount(ComCoverageFixture.DefaultProgId));
    }

    [Fact]
    public async Task ConfigReload_Invalidates_Runtime_Handles_For_Actual_ComRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor();
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.document",
                "det",
                "application",
                [ComCoverageDsl.Step("get", "ActiveDocument")]),
            ComCoverageDsl.Command(
                "det.document.capture",
                "det",
                "handle",
                [ComCoverageDsl.Step("call", "CaptureThread", ComCoverageDsl.Arg("tag", "string"))]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, descriptor);

        CommandExecutionResult first = await ExecuteAsync(harness, "det.document");
        string handleId = AssertResultObject(first)["handleId"]!.GetValue<string>();

        UtilitySettings updated = harness.Settings.Clone();
        updated.ConfigVersion = "coverage-reload";
        updated.ComAdapters.Clear();
        updated.ComAdapters.Add(ComCoverageFixture.CreateDescriptor(configure: static value => value.DisplayName = "Deterministic COM Reloaded"));
        ConfigUpdateResponse response = await harness.ConfigurationManager.ApplyAsync(
            new LoadConfigRequest(updated, Persist: false),
            CancellationToken.None);
        CommandExecutionResult stale = await ExecuteAsync(
            harness,
            "det.document.capture",
            new JsonObject
            {
                ["handleId"] = handleId,
                ["tag"] = "after-reload",
            },
            expectSuccess: false);

        Assert.True(response.Applied);
        Assert.False(stale.Success);
        Assert.Equal("det_not_found", stale.Error?.Code);
        Assert.Contains("was not found", stale.Error?.Message);
    }

    [Fact]
    public async Task Assignments_Are_Idempotent_And_Respect_Visible_And_Realtime_Flags()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor(configure: static value =>
        {
            value.ApplicationAssignments =
            [
                new ComMemberAssignmentDefinition
                {
                    Member = "BaseAssigned",
                    Value = JsonValue.Create(true),
                    IgnoreErrors = false,
                },
                new ComMemberAssignmentDefinition
                {
                    Member = "ThrowOnIgnoredAssignment",
                    Value = JsonValue.Create(true),
                    IgnoreErrors = true,
                },
            ];
            value.VisibleApplicationAssignments =
            [
                new ComMemberAssignmentDefinition
                {
                    Member = "VisibleAssigned",
                    Value = JsonValue.Create(true),
                    IgnoreErrors = false,
                },
            ];
            value.WarmupAssignments =
            [
                new ComMemberAssignmentDefinition
                {
                    Member = "WarmupAssigned",
                    Value = JsonValue.Create(true),
                    IgnoreErrors = false,
                },
            ];
            value.RealtimeAssignments =
            [
                new ComMemberAssignmentDefinition
                {
                    Member = "RealtimeAssigned",
                    Value = JsonValue.Create(true),
                    IgnoreErrors = false,
                },
            ];
        });
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.ping",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "CaptureThread", ComCoverageDsl.Arg("tag", "string"))]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, descriptor);

        await ExecuteAsync(harness, "det.ping", new JsonObject { ["tag"] = "first", ["visible"] = true });
        DeterministicComApplication application = provider.EnsureRunning();

        Assert.True(application.BaseAssigned);
        Assert.True(application.VisibleAssigned);
        Assert.True(application.WarmupAssigned);
        Assert.False(application.RealtimeAssigned);
        Assert.Equal(1, application.BaseAssignmentCount);
        Assert.Equal(1, application.VisibleAssignmentCount);
        Assert.Equal(1, application.WarmupAssignmentCount);
        Assert.Equal(0, application.RealtimeAssignmentCount);

        await ExecuteAsync(harness, "det.ping", new JsonObject { ["tag"] = "second", ["visible"] = true });
        Assert.Equal(1, application.BaseAssignmentCount);
        Assert.Equal(1, application.VisibleAssignmentCount);
        Assert.Equal(1, application.WarmupAssignmentCount);

        await ExecuteAsync(harness, "det.ping", new JsonObject { ["tag"] = "third", ["visible"] = true, ["realtime"] = true });
        Assert.True(application.RealtimeAssigned);
        Assert.Equal(1, application.RealtimeAssignmentCount);
    }

    [Fact]
    public async Task ResultHints_Support_Object_Collection_RuntimeProgId_And_CompactFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor(includeSurfaces: false, configure: static value =>
        {
            value.ResultHints =
            [
                new ComResultHintDefinition
                {
                    Name = "application",
                    MatchCurrentApplicationReference = true,
                    IncludeHandleId = false,
                    RequiredAllMembers = ["AdapterStamp"],
                    Fields =
                    [
                        new ComResultFieldDefinition
                        {
                            Name = "stamp",
                            MemberPaths = ["AdapterStamp"],
                        },
                        new ComResultFieldDefinition
                        {
                            Name = "progId",
                            UseRuntimeProgId = true,
                        },
                    ],
                    CompactFields =
                    [
                        new ComResultFieldDefinition
                        {
                            Name = "stamp",
                            MemberPaths = ["AdapterStamp"],
                        },
                    ],
                },
                new ComResultHintDefinition
                {
                    Name = "info",
                    IncludeHandleId = false,
                    RequiredAllMembers = ["Name", "Path"],
                    Fields =
                    [
                        new ComResultFieldDefinition { Name = "name", MemberPaths = ["Name"] },
                        new ComResultFieldDefinition { Name = "category", MemberPaths = ["Category"] },
                        new ComResultFieldDefinition { Name = "path", MemberPaths = ["Path"] },
                    ],
                    CompactFields =
                    [
                        new ComResultFieldDefinition { Name = "name", MemberPaths = ["Name"] },
                    ],
                },
                new ComResultHintDefinition
                {
                    Name = "items",
                    IsCollection = true,
                    RequiredAllMembers = ["Name"],
                    Fields =
                    [
                        new ComResultFieldDefinition { Name = "name", MemberPaths = ["Name"] },
                        new ComResultFieldDefinition { Name = "value", MemberPaths = ["Value"] },
                    ],
                },
            ];
        });
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command("det.application.hint", "det", "application", []),
            ComCoverageDsl.Command("det.info", "det", "application", [ComCoverageDsl.Step("get", "Info")]),
            ComCoverageDsl.Command("det.items", "det", "application", [ComCoverageDsl.Step("get", "ItemArray")]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, descriptor);

        JsonObject applicationNode = AssertResultObject(await ExecuteAsync(harness, "det.application.hint"));
        JsonObject infoFull = AssertResultObject(await ExecuteAsync(harness, "det.info"));
        JsonObject infoCompact = AssertResultObject(await ExecuteAsync(harness, "det.info", verbosity: ReportVerbosity.Compact));
        CommandExecutionResult itemsResult = await ExecuteAsync(harness, "det.items");

        Assert.Equal("KWB.Deterministic.Application:1", applicationNode["stamp"]?.GetValue<string>());
        Assert.Equal(ComCoverageFixture.DefaultProgId, applicationNode["progId"]?.GetValue<string>());
        Assert.Null(applicationNode["handleId"]);

        Assert.Contains("info-1", infoFull.ToJsonString());
        Assert.Contains("category-1", infoFull.ToJsonString());
        Assert.Null(infoFull["handleId"]);

        Assert.Contains("info-1", infoCompact.ToJsonString());

        Assert.NotNull(itemsResult.Result);
        if (itemsResult.Result is JsonArray items)
        {
            Assert.Equal(3, items.Count);
            Assert.Equal("first", items[0]?["name"]?.GetValue<string>());
            Assert.Equal(1, items[0]?["value"]?.GetValue<int>());
        }
        else
        {
            Assert.Contains("first", itemsResult.Result!.ToJsonString());
        }
    }

    [Fact]
    public async Task ReportVerbosity_Defaults_To_Compact_When_Configured_Or_Realtime_And_Can_Be_Overridden()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor(includeSurfaces: false, configure: static value =>
        {
            value.CompactReportDefault = true;
            value.ResultHints =
            [
                new ComResultHintDefinition
                {
                    Name = "info",
                    IncludeHandleId = false,
                    RequiredAllMembers = ["Name"],
                    Fields =
                    [
                        new ComResultFieldDefinition { Name = "name", MemberPaths = ["Name"] },
                        new ComResultFieldDefinition { Name = "category", MemberPaths = ["Category"] },
                    ],
                    CompactFields =
                    [
                        new ComResultFieldDefinition { Name = "name", MemberPaths = ["Name"] },
                    ],
                },
            ];
        });
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command("det.info", "det", "application", [ComCoverageDsl.Step("get", "Info")]));
        TestDispatcherHarness harness = ComCoverageFixture.CreateHarness(provider, profile, descriptor);

        CommandExecutionResult defaultCompact = await ExecuteAsync(harness, "det.info");
        CommandExecutionResult overriddenFull = await ExecuteAsync(harness, "det.info", verbosity: ReportVerbosity.Full);
        ComInvokeDescriptor realtimeDescriptor = descriptor.Clone();
        realtimeDescriptor.CompactReportDefault = false;
        TestDispatcherHarness realtimeHarness = ComCoverageFixture.CreateHarness(provider, profile, realtimeDescriptor);
        CommandExecutionResult realtimeCompact = await ExecuteAsync(
            realtimeHarness,
            "det.info",
            new JsonObject { ["realtime"] = true });

        Assert.Equal("Compact", defaultCompact.Report?["reportVerbosity"]?.GetValue<string>());
        Assert.Null(defaultCompact.Report?["arguments"]);
        Assert.NotNull(defaultCompact.Report?["argumentKeys"]);
        Assert.Equal("Full", overriddenFull.Report?["reportVerbosity"]?.GetValue<string>());
        Assert.NotNull(overriddenFull.Report?["arguments"]);
        Assert.Equal("Compact", realtimeCompact.Report?["reportVerbosity"]?.GetValue<string>());
    }

    [Fact]
    public void ErrorMapping_Uses_Configured_Codes_For_NotFound_Com_Invoke_And_Unavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        ComInvokeDescriptor descriptor = ComCoverageFixture.CreateDescriptor();
        ComInvokeRuntime runtime = new(
            descriptor,
            new ComInvokeRuntimeHooks
            {
                AcquireApplication = provider.Acquire,
                ReleaseApplication = provider.Release,
            });

        CommandExecutionResult notFound = runtime.MapInvokeException(
            "det.throw.not-found",
            new InvalidOperationException("Document not found for deterministic fixture."),
            new InvokeDefinition());
        CommandExecutionResult invoke = runtime.MapInvokeException(
            "det.throw.invoke",
            new InvalidOperationException("Deterministic invoke failure."),
            new InvokeDefinition());
        Exception comException = Marshal.GetExceptionForHR(unchecked((int)0x80010001))
            ?? new InvalidOperationException("Expected COM exception for HRESULT 0x80010001.");
        CommandExecutionResult com = runtime.MapInvokeException(
            "det.throw.call-rejected",
            comException,
            new InvokeDefinition());
        CommandExecutionResult unavailable = runtime.MapInvokeException(
            "det.application",
            new InvalidOperationException("Requested COM ProgID is not registered."),
            new InvokeDefinition());

        Assert.Equal("det_not_found", notFound.Error?.Code);
        Assert.Equal("det_invoke_failed", invoke.Error?.Code);
        Assert.Equal("det_com_error", com.Error?.Code);
        Assert.Equal("det_unavailable", unavailable.Error?.Code);
    }

    [Fact]
    public async Task Dispatcher_Serializes_Per_Adapter_And_Uses_Different_STA_Threads_For_Different_Adapters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeterministicComApplicationProvider provider = new();
        provider.RegisterProgId("KWB.Deterministic.Application.Two");
        provider.EnsureRunning();
        provider.EnsureRunning("KWB.Deterministic.Application.Two");

        ComInvokeDescriptor first = ComCoverageFixture.CreateDescriptor();
        ComInvokeDescriptor second = ComCoverageFixture.CreateDescriptor(configure: static value =>
        {
            value.AdapterName = "det2";
            value.DisplayName = "Deterministic COM 2";
            value.DefaultProgIds = ["KWB.Deterministic.Application.Two"];
        });
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            ComCoverageDsl.Command(
                "det.capture",
                "det",
                "application",
                [ComCoverageDsl.Step("call", "CaptureThread", ComCoverageDsl.Arg("tag", "string"), ComCoverageDsl.Arg("delayMs", "int"))]),
            ComCoverageDsl.Command(
                "det2.capture",
                "det2",
                "application",
                [ComCoverageDsl.Step("call", "CaptureThread", ComCoverageDsl.Arg("tag", "string"), ComCoverageDsl.Arg("delayMs", "int"))]));
        UtilitySettings settings = TestDsl.Settings(profile, first, second);
        FakeManifestService manifestService = new();
        FakeClock clock = new(DateTimeOffset.Parse("2026-03-11T00:00:00+00:00", CultureInfo.InvariantCulture));
        RuntimeConfigurationManager configurationManager = new(
            settings,
            manifestService,
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeConfigurationManager>.Instance);
        SystemRuntime systemRuntime = new(settings);
        SystemInvokeSurface systemSurface = new(systemRuntime);
        AdapterInvokeSurfaceRegistry registry = new(
            configurationManager,
            systemSurface,
            new DeterministicComInvokeSurfaceFactory(provider));
        CommandPlanCompiler compiler = new(registry, configurationManager);
        CommandDispatcher dispatcher = new(
            new ProfileStore(settings),
            compiler,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandDispatcher>.Instance);
        TestDispatcherHarness harness = new()
        {
            Settings = settings,
            ConfigurationManager = configurationManager,
            Dispatcher = dispatcher,
        };

        Task<CommandExecutionResult>[] sameAdapterTasks =
        [
            ExecuteAsync(harness, "det.capture", new JsonObject { ["tag"] = "a", ["delayMs"] = 25 }),
            ExecuteAsync(harness, "det.capture", new JsonObject { ["tag"] = "b", ["delayMs"] = 25 }),
            ExecuteAsync(harness, "det.capture", new JsonObject { ["tag"] = "c", ["delayMs"] = 25 }),
        ];
        CommandExecutionResult[] sameAdapterResults = await Task.WhenAll(sameAdapterTasks);
        int[] sameAdapterThreadIds = sameAdapterResults
            .Select(result => ParseThreadId(result.Result?.GetValue<string>()))
            .Distinct()
            .ToArray();

        Task<CommandExecutionResult> firstTask = ExecuteAsync(harness, "det.capture", new JsonObject { ["tag"] = "x", ["delayMs"] = 50 });
        Task<CommandExecutionResult> secondTask = ExecuteAsync(harness, "det2.capture", new JsonObject { ["tag"] = "y", ["delayMs"] = 50 });
        CommandExecutionResult[] differentAdapterResults = await Task.WhenAll(firstTask, secondTask);
        int firstThread = ParseThreadId(differentAdapterResults[0].Result?.GetValue<string>());
        int secondThread = ParseThreadId(differentAdapterResults[1].Result?.GetValue<string>());

        Assert.Single(sameAdapterThreadIds);
        Assert.NotEqual(firstThread, secondThread);
    }

    private static async Task<CommandExecutionResult> ExecuteAsync(
        TestDispatcherHarness harness,
        string commandId,
        JsonObject? arguments = null,
        ReportVerbosity? verbosity = null,
        string? sharedContextId = null,
        bool expectSuccess = true)
    {
        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                commandId,
                arguments ?? new JsonObject(),
                verbosity,
                sharedContextId),
            CancellationToken.None);

        if (expectSuccess)
        {
            Assert.True(result.Success, result.Error?.Message);
        }
        else
        {
            Assert.False(result.Success);
        }

        return result;
    }

    private static JsonObject AssertResultObject(CommandExecutionResult result)
        => Assert.IsType<JsonObject>(result.Result);

    private static int ParseThreadId(string? payload)
    {
        Assert.False(string.IsNullOrWhiteSpace(payload));
        string[] parts = payload!.Split(':');
        return int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}
