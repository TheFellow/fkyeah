module Sprint007FidelityTests

open System
open System.IO
open System.Text.Json
open Xunit
open Attractor

let private createTempDir () =
    let dir = Path.Combine(Path.GetTempPath(), $"attractor-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    dir

[<Fact>]
let ``FidelityMode exposes sprint 007 token and character budgets`` () =
    Assert.Equal(Int32.MaxValue, FidelityMode.tokenBudget FidelityMode.Full)
    Assert.Equal(100, FidelityMode.tokenBudget FidelityMode.Truncate)
    Assert.Equal(800, FidelityMode.tokenBudget FidelityMode.Compact)
    Assert.Equal(600, FidelityMode.tokenBudget FidelityMode.SummaryLow)
    Assert.Equal(1500, FidelityMode.tokenBudget FidelityMode.SummaryMedium)
    Assert.Equal(3000, FidelityMode.tokenBudget FidelityMode.SummaryHigh)
    Assert.Equal(400, FidelityMode.charBudget FidelityMode.Truncate)
    Assert.Equal(12000, FidelityMode.charBudget FidelityMode.SummaryHigh)
    Assert.False(FidelityMode.useFreshSession FidelityMode.Full)
    Assert.True(FidelityMode.useFreshSession FidelityMode.Compact)

[<Fact>]
let ``Context Project uses token-derived defaults and explicit overrides`` () =
    let ctx = Context()
    ctx.Set("long", String.replicate 1000 "x")

    let projectedDefault = ctx.Project(FidelityMode.Truncate)
    let projectedExplicit = ctx.Project(FidelityMode.Truncate, truncateLimit = 500)

    Assert.Equal(400, projectedDefault.Get("long").Length)
    Assert.Equal(500, projectedExplicit.Get("long").Length)

[<Fact>]
let ``Compact projection preserves structural keys under budget pressure`` () =
    let ctx = Context()
    ctx.Set("graph.goal", String.replicate 200 "g")
    ctx.Set("current_node", "implement")
    ctx.Set("outcome", "success")

    for index in 1..20 do
        ctx.Set($"payload.{index}", String.replicate 500 "x")

    let projected = ctx.Project(FidelityMode.Compact)

    Assert.Equal(String.replicate 200 "g", projected.Get("graph.goal"))
    Assert.Equal("implement", projected.Get("current_node"))
    Assert.Equal("success", projected.Get("outcome"))
    Assert.True(projected.Count < ctx.Count)

[<Fact>]
let ``invalid node and graph fidelity fall back to Compact`` () =
    let graphInvalid =
        { Name = "test"
          Nodes = Map.empty
          Edges = []
          GraphAttributes = Map.ofList [ "default_fidelity", AttrValue.String "summary:invalid" ] }

    let nodeInvalid =
        { Id = "node"
          Attributes = Map.ofList [ "fidelity", AttrValue.String "bogus" ] }

    Assert.Equal(FidelityMode.Compact, FidelityResolution.resolve None nodeInvalid graphInvalid)

    let nodeEmpty = { Id = "node"; Attributes = Map.empty }
    Assert.Equal(FidelityMode.Compact, FidelityResolution.resolve None nodeEmpty graphInvalid)

[<Fact>]
let ``preparePromptContext prioritizes high value keys and records truncation`` () =
    let ctx = Context()
    ctx.Set("graph.goal", "ship feature")
    ctx.Set("graph.name", "pipeline")
    ctx.Set("current_node", "implement")
    ctx.Set("outcome", "success")
    ctx.Set("tool.output", String.replicate 1000 "t")
    ctx.Set("last_response", String.replicate 1000 "r")
    ctx.Set("parallel.branch.a", "done")
    ctx.Set("human.gate.input", "approved")

    let prepared =
        ContextPrompt.preparePromptContext FidelityMode.Truncate ctx "ship feature"

    Assert.Contains("tool.output", prepared.IncludedKeys)
    Assert.Contains("tool.output", prepared.TruncatedKeys)
    Assert.Contains("last_response", prepared.ExcludedKeys)
    Assert.True(prepared.CharBudgetUsed <= FidelityMode.charBudget FidelityMode.Truncate)
    Assert.Contains("## Tool Output (from previous stage)", prepared.SystemMessage)

[<Fact>]
let ``SummaryHigh prompt preparation includes context summary`` () =
    let ctx = Context()
    ctx.Set("context_summary", "graph.goal=ship; outcome=success")

    let prepared =
        ContextPrompt.preparePromptContext FidelityMode.SummaryHigh ctx "ship"

    Assert.Contains("context_summary", prepared.IncludedKeys)
    Assert.Contains("context_summary", prepared.SystemMessage)

[<Fact>]
let ``Codergen handler writes context_budget artifact`` () =
    let handler = Handlers.CodergenHandler() :> IHandler

    let node =
        { Id = "implement"
          Attributes =
            Map.ofList
                [ "shape", AttrValue.String "box"
                  "prompt", AttrValue.String "Implement"
                  "__resolved_fidelity", AttrValue.String "truncate" ] }

    let ctx = Context()
    ctx.Set("tool.output", String.replicate 1000 "x")

    let graph =
        { Name = "test"
          Nodes = Map.empty
          Edges = []
          GraphAttributes = Map.ofList [ "goal", AttrValue.String "ship feature" ] }

    let logsRoot = createTempDir ()

    let outcome = handler.Execute(node, ctx, graph, logsRoot)
    Assert.Equal(StageStatus.Success, outcome.Status)

    let budgetPath = Path.Combine(logsRoot, "implement", "context_budget.json")
    Assert.True(File.Exists(budgetPath))

    use doc = JsonDocument.Parse(File.ReadAllText(budgetPath))
    let root = doc.RootElement
    Assert.Equal("truncate", root.GetProperty("fidelity_mode").GetString())
    Assert.Equal(100, root.GetProperty("token_budget").GetInt32())
    Assert.Equal(400, root.GetProperty("char_budget").GetInt32())
    Assert.True(root.GetProperty("char_budget_used").GetInt32() <= 400)
    Assert.True(root.GetProperty("fresh_session").GetBoolean())
    Assert.True(root.GetProperty("included_keys").ValueKind = JsonValueKind.Array)
    Assert.True(root.GetProperty("truncated_keys").ValueKind = JsonValueKind.Array)
    Assert.True(root.GetProperty("excluded_keys").ValueKind = JsonValueKind.Array)
