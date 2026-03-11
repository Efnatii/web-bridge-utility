using System.Text.Json.Nodes;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

public sealed class RuntimeDslReloadTests
{
    [Fact]
    public async Task ConfigLoad_HotAdds_ComAdapter_Without_Restart()
    {
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            TestDsl.Command("kompas.adapter-stamp", "kompas", "application", TestDsl.Step("get", "AdapterStamp")));
        UtilitySettings settings = TestDsl.Settings(profile);
        TestDispatcherHarness harness = TestDsl.CreateDispatcherHarness(
            settings,
            descriptor => new TestAdapterRoot($"{descriptor.DisplayName}:{descriptor.Surfaces.Count}"));

        CommandExecutionResult before = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.adapter-stamp", new JsonObject()),
            CancellationToken.None);
        Assert.False(before.Success);

        UtilitySettings next = settings.Clone();
        next.ComAdapters.Add(CreateAdapter("KOMPAS v1", 1));
        ConfigUpdateResponse response = await harness.ConfigurationManager.ApplyAsync(
            new LoadConfigRequest(next, Persist: false),
            CancellationToken.None);

        Assert.True(response.Applied);
        CommandExecutionResult after = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.adapter-stamp", new JsonObject()),
            CancellationToken.None);

        Assert.True(after.Success, after.Error?.Message);
        Assert.Equal("KOMPAS v1:1", after.Result?.GetValue<string>());
    }

    [Fact]
    public async Task AdapterReload_Invalidates_Compiled_Command_Plans()
    {
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            TestDsl.Command("kompas.adapter-stamp", "kompas", "application", TestDsl.Step("get", "AdapterStamp")));
        UtilitySettings settings = TestDsl.Settings(profile, CreateAdapter("KOMPAS v1", 1));
        TestDispatcherHarness harness = TestDsl.CreateDispatcherHarness(
            settings,
            descriptor => new TestAdapterRoot($"{descriptor.DisplayName}:{descriptor.Surfaces.Count}"));

        CommandExecutionResult first = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.adapter-stamp", new JsonObject()),
            CancellationToken.None);
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal("KOMPAS v1:1", first.Result?.GetValue<string>());

        UtilitySettings next = settings.Clone();
        next.ComAdapters.Clear();
        next.ComAdapters.Add(CreateAdapter("KOMPAS v2", 2));
        await harness.ConfigurationManager.ApplyAsync(
            new LoadConfigRequest(next, Persist: false),
            CancellationToken.None);

        CommandExecutionResult second = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.adapter-stamp", new JsonObject()),
            CancellationToken.None);

        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal("KOMPAS v2:2", second.Result?.GetValue<string>());
    }

    [Fact]
    public async Task Existing_Com_Commands_Work_When_Surfaces_Are_Absent()
    {
        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            TestDsl.Command("legacy.title", "kompas", "application", TestDsl.Step("get", "Title")));
        UtilitySettings settings = TestDsl.Settings(
            profile,
            new ComInvokeDescriptor
            {
                AdapterName = "kompas",
                DisplayName = "Legacy",
                InvokeErrorCode = "kompas_invoke_failed",
                ComErrorCode = "kompas_com_error",
            });
        TestDispatcherHarness harness = TestDsl.CreateDispatcherHarness(
            settings,
            _ => new LegacyComRoot("legacy-ok"));

        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "legacy.title", new JsonObject()),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("legacy-ok", result.Result?.GetValue<string>());
    }

    [Fact]
    public async Task Kompas_Table_Scenario_Works_Through_Pure_Dsl()
    {
        FakeView view = new("Main");
        FakeViews views = new(view);
        FakeViewsAndLayersManager manager = new(views);
        FakeDocument2D document = new(manager);
        FakeApplicationRoot application = new(document);
        FakeDrawingTables drawingTables = Assert.IsType<FakeDrawingTables>(view.DrawingTables);

        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            TestDsl.Command(
                "kompas.table.write-cell",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("cast", "IFakeSymbols2DContainer"),
                TestDsl.Step("get", "DrawingTables"),
                TestDsl.Step("call", "Add", TestDsl.Arg("rows", "int"), TestDsl.Arg("cols", "int")),
                TestDsl.Step("cast", "IFakeTable"),
                TestDsl.IndexStep(TestDsl.Arg("row", "int"), TestDsl.Arg("col", "int")),
                TestDsl.Step("get", "Text"),
                TestDsl.SetStep("Str", "value"),
                TestDsl.Step("get", "Str")),
            TestDsl.Command(
                "kompas.document.save",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("call", "Save", TestDsl.Arg("path", "string"))),
            TestDsl.Command(
                "kompas.table.load",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("cast", "IFakeSymbols2DContainer"),
                TestDsl.Step("get", "DrawingTables"),
                TestDsl.Step("call", "Load", TestDsl.Arg("path", "string"))));
        UtilitySettings settings = TestDsl.Settings(profile, CreateKompasAdapter());
        TestDispatcherHarness harness = TestDsl.CreateDispatcherHarness(settings, _ => application);

        CommandExecutionResult write = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                "kompas.table.write-cell",
                new JsonObject
                {
                    ["rows"] = 2,
                    ["cols"] = 2,
                    ["row"] = 1,
                    ["col"] = 1,
                    ["value"] = "hello",
                }),
            CancellationToken.None);
        CommandExecutionResult save = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                "kompas.document.save",
                new JsonObject { ["path"] = "drawing.cdw" }),
            CancellationToken.None);
        CommandExecutionResult load = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                "kompas.table.load",
                new JsonObject { ["path"] = "table.tbl" }),
            CancellationToken.None);

        Assert.True(write.Success, write.Error?.Message);
        Assert.Equal("hello", write.Result?.GetValue<string>());
        Assert.True(save.Success, save.Error?.Message);
        Assert.True(load.Success, load.Error?.Message);
        Assert.Equal("drawing.cdw", document.LastSavedPath);
        Assert.Contains("table.tbl", drawingTables.LoadedPaths);
    }

    [Fact]
    public async Task Kompas_Table_Scenario_Works_With_KompasLike_Table_Signatures()
    {
        FakeView view = new("Main");
        FakeViews views = new(view);
        FakeViewsAndLayersManager manager = new(views);
        FakeDocument2D document = new(manager);
        FakeApplicationRoot application = new(document);

        ProfileDefinition profile = TestDsl.Profile(
            "runtime",
            TestDsl.Command(
                "kompas.table.write-cell.kompas-shape",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("cast", "IFakeSymbols2DContainer"),
                TestDsl.Step("get", "DrawingTables"),
                TestDsl.Step(
                    "call",
                    "Add",
                    TestDsl.Arg("rows", "int"),
                    TestDsl.Arg("cols", "int"),
                    TestDsl.Arg("rowHeight", "double"),
                    TestDsl.Arg("colWidth", "double"),
                    TestDsl.Arg("titlePos", "int")),
                TestDsl.Step("cast", "IFakeTable"),
                TestDsl.Step("index", "Cell", TestDsl.Arg("row", "int"), TestDsl.Arg("col", "int")),
                TestDsl.Step("get", "Text"),
                TestDsl.Step("cast", "IFakeText"),
                TestDsl.SetStep("Str", "value"),
                TestDsl.Step("get", "Str")));
        UtilitySettings settings = TestDsl.Settings(profile, CreateKompasAdapter());
        TestDispatcherHarness harness = TestDsl.CreateDispatcherHarness(settings, _ => application);

        CommandExecutionResult write = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                "kompas.table.write-cell.kompas-shape",
                new JsonObject
                {
                    ["rows"] = 2,
                    ["cols"] = 2,
                    ["rowHeight"] = 10.0,
                    ["colWidth"] = 40.0,
                    ["titlePos"] = 2,
                    ["row"] = 1,
                    ["col"] = 1,
                    ["value"] = "hello",
                }),
            CancellationToken.None);

        Assert.True(write.Success, write.Error?.Message);
        Assert.Equal("hello", write.Result?.GetValue<string>());
    }

    private static ComInvokeDescriptor CreateAdapter(string displayName, int surfacesCount)
    {
        return new ComInvokeDescriptor
        {
            AdapterName = "kompas",
            DisplayName = displayName,
            InvokeErrorCode = "kompas_invoke_failed",
            ComErrorCode = "kompas_com_error",
            Surfaces = Enumerable.Range(1, surfacesCount)
                .Select(index => new ComSurfaceDefinition
                {
                    Name = $"ITestSurface{index}",
                    ClrTypeName = typeof(TestAdapterRoot).AssemblyQualifiedName,
                })
                .ToList(),
        };
    }

    private static ComInvokeDescriptor CreateKompasAdapter()
    {
        return new ComInvokeDescriptor
        {
            AdapterName = "kompas",
            DisplayName = "KOMPAS",
            InvokeErrorCode = "kompas_invoke_failed",
            ComErrorCode = "kompas_com_error",
            Surfaces =
            [
                Surface("IFakeDocument2D", typeof(IFakeDocument2D), "{11111111-1111-1111-1111-111111111111}"),
                Surface("IFakeViewsAndLayersManager", typeof(IFakeViewsAndLayersManager), "{22222222-2222-2222-2222-222222222222}"),
                Surface("IFakeViews", typeof(IFakeViews), "{33333333-3333-3333-3333-333333333333}"),
                Surface("IFakeView", typeof(IFakeView), "{44444444-4444-4444-4444-444444444444}"),
                Surface("IFakeSymbols2DContainer", typeof(IFakeSymbols2DContainer), "{55555555-5555-5555-5555-555555555555}"),
                Surface("IFakeDrawingTables", typeof(IFakeDrawingTables), "{66666666-6666-6666-6666-666666666666}"),
                Surface("IFakeDrawingTable", typeof(IFakeDrawingTable), "{77777777-7777-7777-7777-777777777777}"),
                Surface("IFakeTable", typeof(IFakeTable), "{88888888-8888-8888-8888-888888888888}"),
                Surface("IFakeTableCell", typeof(IFakeTableCell), "{99999999-9999-9999-9999-999999999999}"),
                Surface("IFakeText", typeof(IFakeText), "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"),
            ],
        };
    }

    private static ComSurfaceDefinition Surface(string name, Type type, string iid)
    {
        return new ComSurfaceDefinition
        {
            Name = name,
            ClrTypeName = type.AssemblyQualifiedName,
            Iid = iid,
            Aliases = [name, type.Name],
        };
    }

    private sealed class LegacyComRoot(string title)
    {
        public string Title { get; } = title;
    }

}
