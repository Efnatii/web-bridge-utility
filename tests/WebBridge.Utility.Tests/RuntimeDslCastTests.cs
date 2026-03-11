using System.Text.Json.Nodes;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

public sealed class RuntimeDslCastTests
{
    [Fact]
    public async Task Cast_Succeeds_ForConfiguredSurface()
    {
        TestDispatcherHarness harness = CreateHarness(
            TestDsl.Command(
                "kompas.cast.symbols",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("cast", "IFakeSymbols2DContainer")));

        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.cast.symbols", new JsonObject()),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        JsonObject node = Assert.IsType<JsonObject>(result.Result);
        Assert.Equal("IFakeSymbols2DContainer", node["surface"]?.GetValue<string>());
        Assert.Contains("IFakeSymbols2DContainer", node["resolvedInterfaces"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task Cast_Failure_Returns_DescriptiveError()
    {
        TestDispatcherHarness harness = CreateHarness(
            TestDsl.Command(
                "kompas.cast.missing",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("cast", "IFakeMissingSurface")));

        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.cast.missing", new JsonObject()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("IFakeMissingSurface", result.Error!.Message);
        Assert.Contains("IFakeSymbols2DContainer", result.Error.Message);
        Assert.Contains("FakeView", result.Error.Message);
    }

    [Fact]
    public async Task TryCast_Returns_Null_WhenSurfaceIsUnavailable()
    {
        TestDispatcherHarness harness = CreateHarness(
            TestDsl.Command(
                "kompas.try-cast.missing",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("tryCast", "IFakeMissingSurface")));

        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.try-cast.missing", new JsonObject()),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task QueryInterface_Uses_Generic_Com_Surface_Resolution()
    {
        TestDispatcherHarness harness = CreateHarness(
            TestDsl.Command(
                "kompas.query-interface",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView"),
                TestDsl.Step("queryInterface", SymbolsSurfaceIid)));

        CommandExecutionResult result = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.query-interface", new JsonObject()),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        JsonObject node = Assert.IsType<JsonObject>(result.Result);
        Assert.Equal("IFakeSymbols2DContainer", node["surface"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExistingHandle_CanBe_Recast_In_FollowUp_Command()
    {
        TestDispatcherHarness harness = CreateHarness(
            TestDsl.Command(
                "kompas.view",
                "kompas",
                "application",
                TestDsl.Step("get", "ActiveDocument"),
                TestDsl.Step("get", "ViewsAndLayersManager"),
                TestDsl.Step("get", "Views"),
                TestDsl.Step("get", "ActiveView")),
            TestDsl.Command(
                "kompas.view.cast.symbols",
                "kompas",
                "handle",
                TestDsl.Step("cast", "IFakeSymbols2DContainer")));

        CommandExecutionResult first = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest("runtime", "kompas.view", new JsonObject()),
            CancellationToken.None);
        Assert.True(first.Success, first.Error?.Message);
        string handleId = Assert.IsType<JsonObject>(first.Result)["handleId"]!.GetValue<string>();

        CommandExecutionResult second = await harness.Dispatcher.ExecuteAsync(
            new ExecuteCommandRequest(
                "runtime",
                "kompas.view.cast.symbols",
                new JsonObject { ["handleId"] = handleId }),
            CancellationToken.None);

        Assert.True(second.Success, second.Error?.Message);
        JsonObject node = Assert.IsType<JsonObject>(second.Result);
        Assert.Equal("IFakeSymbols2DContainer", node["surface"]?.GetValue<string>());
        Assert.Contains("IFakeView", node["resolvedInterfaces"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains("IFakeSymbols2DContainer", node["resolvedInterfaces"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    private static TestDispatcherHarness CreateHarness(params CommandDefinition[] commands)
    {
        ProfileDefinition profile = TestDsl.Profile("runtime", commands);
        UtilitySettings settings = TestDsl.Settings(profile, CreateKompasAdapter());
        return TestDsl.CreateDispatcherHarness(settings, _ => CreateFakeApplication());
    }

    private static FakeApplicationRoot CreateFakeApplication()
    {
        FakeView view = new("Main");
        FakeViews views = new(view);
        FakeViewsAndLayersManager manager = new(views);
        FakeDocument2D document = new(manager);
        return new FakeApplicationRoot(document);
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
                Surface("IFakeSymbols2DContainer", typeof(IFakeSymbols2DContainer), SymbolsSurfaceIid),
                Surface("IFakeDrawingTables", typeof(IFakeDrawingTables), "{66666666-6666-6666-6666-666666666666}"),
                Surface("IFakeDrawingTable", typeof(IFakeDrawingTable), "{77777777-7777-7777-7777-777777777777}"),
                Surface("IFakeTable", typeof(IFakeTable), "{88888888-8888-8888-8888-888888888888}"),
                Surface("IFakeTableCell", typeof(IFakeTableCell), "{99999999-9999-9999-9999-999999999999}"),
                Surface("IFakeText", typeof(IFakeText), "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"),
                Surface("IFakeMissingSurface", typeof(IFakeMissingSurface), "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"),
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

    private const string SymbolsSurfaceIid = "{55555555-5555-5555-5555-555555555555}";
}
