module Attractor.CostAggregationTests

open System
open System.IO
open Xunit
open Attractor

[<Fact>]
let ``engine writes cost summary artifact even when no llm cost is present`` () =
    let logsRoot = Path.Combine(Path.GetTempPath(), "attractor-cost-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(logsRoot) |> ignore
    try
        let source = """
digraph T {
  graph [goal="test"]
  start [shape=Mdiamond]
  exit [shape=Msquare]
  start -> exit
}
"""
        let result = Pipeline.runFromSource source (RunConfig.Default(logsRoot))
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.True(File.Exists(Path.Combine(logsRoot, "cost-summary.json")))
    finally
        if Directory.Exists(logsRoot) then Directory.Delete(logsRoot, true)

// ── New Sprint-010 tests ──

let private runTrivialPipeline () =
    let logsRoot = Path.Combine(Path.GetTempPath(), "attractor-cost-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(logsRoot) |> ignore
    let source = """
digraph T {
  graph [goal="test"]
  start [shape=Mdiamond]
  exit [shape=Msquare]
  start -> exit
}
"""
    let result = Pipeline.runFromSource source (RunConfig.Default(logsRoot))
    logsRoot, result

[<Fact>]
let ``cost-summary json contains valid JSON`` () =
    let logsRoot, _ = runTrivialPipeline()
    try
        let json = File.ReadAllText(Path.Combine(logsRoot, "cost-summary.json"))
        let doc = System.Text.Json.JsonDocument.Parse(json)
        Assert.NotNull(doc)
    finally
        if Directory.Exists(logsRoot) then Directory.Delete(logsRoot, true)

[<Fact>]
let ``cost-summary json contains totalCostMicrodollars field`` () =
    let logsRoot, _ = runTrivialPipeline()
    try
        let json = File.ReadAllText(Path.Combine(logsRoot, "cost-summary.json"))
        let doc = System.Text.Json.JsonDocument.Parse(json)
        let mutable elem = Unchecked.defaultof<System.Text.Json.JsonElement>
        Assert.True(doc.RootElement.TryGetProperty("totalCostMicrodollars", &elem), "expected totalCostMicrodollars")
    finally
        if Directory.Exists(logsRoot) then Directory.Delete(logsRoot, true)

[<Fact>]
let ``trivial pipeline succeeds with zero cost`` () =
    let logsRoot, result = runTrivialPipeline()
    try
        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        let json = File.ReadAllText(Path.Combine(logsRoot, "cost-summary.json"))
        let doc = System.Text.Json.JsonDocument.Parse(json)
        let totalMicros = doc.RootElement.GetProperty("totalCostMicrodollars").GetInt64()
        Assert.Equal(0L, totalMicros)
    finally
        if Directory.Exists(logsRoot) then Directory.Delete(logsRoot, true)

[<Fact>]
let ``cost-summary json contains callCount field`` () =
    let logsRoot, _ = runTrivialPipeline()
    try
        let json = File.ReadAllText(Path.Combine(logsRoot, "cost-summary.json"))
        let doc = System.Text.Json.JsonDocument.Parse(json)
        let mutable elem = Unchecked.defaultof<System.Text.Json.JsonElement>
        Assert.True(doc.RootElement.TryGetProperty("callCount", &elem), "expected callCount field")
    finally
        if Directory.Exists(logsRoot) then Directory.Delete(logsRoot, true)
