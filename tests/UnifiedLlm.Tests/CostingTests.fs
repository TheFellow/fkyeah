module UnifiedLlm.CostingTests

open Xunit
open UnifiedLlm

[<Fact>]
let ``calculate cost uses model catalog pricing`` () =
    let usage =
        { InputTokens = 1000
          OutputTokens = 500
          ReasoningTokens = None
          CacheReadTokens = None
          CacheWriteTokens = None }

    let breakdown =
        Costing.tryCalculateCostById "gpt-5.4" usage false
        |> Option.defaultWith (fun () -> failwith "expected model")

    Assert.True(breakdown.TotalMicrodollars > 0L)
    Assert.Equal("gpt-5.4", breakdown.Model)

[<Fact>]
let ``cached cost replay is not billed twice`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 10
            OutputTokens = 5
            CacheReadTokens = Some 10 }

    let breakdown =
        Costing.tryCalculateCostById "gpt-5.4" usage true
        |> Option.defaultWith (fun () -> failwith "expected model")

    Assert.Equal(0L, breakdown.TotalMicrodollars)

[<Fact>]
let ``cost ledger accumulates totals`` () =
    let ledger = CostLedger.inMemory ()

    let usage =
        { Usage.Zero with
            InputTokens = 10
            OutputTokens = 5 }

    let cost = Costing.tryCalculateCostById "gpt-5.4" usage false |> Option.get
    ledger.Record cost
    ledger.Record cost
    Assert.Equal(2, ledger.CallCount())
    Assert.Equal(cost.TotalMicrodollars * 2L, ledger.TotalMicrodollars())

// ── New Sprint-010 tests ──

[<Fact>]
let ``calculateCost with zero usage returns zero cost`` () =
    let breakdown =
        Costing.tryCalculateCostById "gpt-5.4" Usage.Zero false
        |> Option.defaultWith (fun () -> failwith "expected model")

    Assert.Equal(0L, breakdown.TotalMicrodollars)
    Assert.Equal(0L, breakdown.InputMicrodollars)
    Assert.Equal(0L, breakdown.OutputMicrodollars)

[<Fact>]
let ``calculateCost with known pricing computes correct input cost`` () =
    // claude-opus-4-6: InputCostPerMillion = 5.0
    // 1000 input tokens at $5/M = $0.005 = 5000 microdollars
    let usage = { Usage.Zero with InputTokens = 1000 }

    let breakdown =
        Costing.tryCalculateCostById "claude-opus-4-6" usage false
        |> Option.defaultWith (fun () -> failwith "expected model")

    Assert.Equal(5000L, breakdown.InputMicrodollars)

[<Fact>]
let ``summary format contains Total and token counts`` () =
    let ledger = CostLedger.inMemory ()

    let usage =
        { Usage.Zero with
            InputTokens = 100
            OutputTokens = 50 }

    let cost = Costing.tryCalculateCostById "gpt-5.4" usage false |> Option.get
    ledger.Record cost
    let summary = ledger.Summary()
    Assert.Contains("Total:", summary)
    Assert.Contains("100", summary)
    Assert.Contains("50", summary)

[<Fact>]
let ``ledger thread safety with 100 concurrent records`` () =
    let ledger = CostLedger.inMemory ()

    let usage =
        { Usage.Zero with
            InputTokens = 10
            OutputTokens = 5 }

    let cost = Costing.tryCalculateCostById "gpt-5.4" usage false |> Option.get

    let tasks =
        [| for _ in 1..100 -> System.Threading.Tasks.Task.Run(fun () -> ledger.Record cost) |]

    System.Threading.Tasks.Task.WaitAll(tasks)
    Assert.Equal(100, ledger.CallCount())
    Assert.Equal(cost.TotalMicrodollars * 100L, ledger.TotalMicrodollars())

[<Fact>]
let ``micro-dollar precision with large token count does not overflow`` () =
    // 1_000_000 input tokens at $5/M = $5.00 = 5_000_000 microdollars
    let usage =
        { Usage.Zero with
            InputTokens = 1_000_000 }

    let breakdown =
        Costing.tryCalculateCostById "claude-opus-4-6" usage false
        |> Option.defaultWith (fun () -> failwith "expected model")

    Assert.Equal(5_000_000L, breakdown.InputMicrodollars)
    Assert.True(breakdown.TotalMicrodollars > 0L)
