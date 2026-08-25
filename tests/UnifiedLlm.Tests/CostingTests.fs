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

[<Fact>]
let ``detailed estimate accounts for Anthropic cache reads and writes separately`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 1_000_000
            CacheReadTokens = Some 1_000_000
            CacheWriteTokens = Some 1_000_000 }

    let estimate = CostEstimate.Standard usage

    let cost =
        Costing.tryEstimateCostById "claude-opus-4-6" estimate
        |> Option.defaultWith (fun () -> failwith "expected pricing")

    Assert.Equal(5_000_000L, cost.InputMicrodollars)
    Assert.Equal(0L, cost.CachedInputMicrodollars)
    Assert.Equal(500_000L, cost.CacheReadMicrodollars)
    Assert.Equal(6_250_000L, cost.CacheWriteMicrodollars)
    Assert.Equal(11_750_000L, cost.TotalMicrodollars)

[<Fact>]
let ``one-hour cache writes select the one-hour rate`` () =
    let estimate =
        { CostEstimate.Standard
              { Usage.Zero with
                  CacheWriteTokens = Some 1_000_000 } with
            CacheWriteDuration = CacheWriteDuration.OneHour }

    let cost = Costing.tryEstimateCostById "claude-opus-4-6" estimate |> Option.get

    Assert.Equal(10_000_000L, cost.CacheWriteMicrodollars)

[<Fact>]
let ``inclusive cached input is subtracted before charging normal input`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 1000
            CacheReadTokens = Some 400 }

    let cost =
        Costing.tryEstimateCostById "gpt-5.4" (CostEstimate.Standard usage)
        |> Option.get

    Assert.Equal(21_000L, cost.InputMicrodollars)
    Assert.Equal(1_400L, cost.CachedInputMicrodollars)
    Assert.Equal(22_400L, cost.TotalMicrodollars)

[<Fact>]
let ``cached tokens exceeding inclusive input are not subtracted and produce a note`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 100
            CacheReadTokens = Some 200 }

    let cost =
        Costing.tryEstimateCostById "gpt-5.4" (CostEstimate.Standard usage)
        |> Option.get

    Assert.Equal(3_500L, cost.InputMicrodollars)
    Assert.Equal(700L, cost.CachedInputMicrodollars)
    Assert.Contains(cost.Notes, fun note -> note.Contains("subtraction was skipped"))

[<Fact>]
let ``batch tier applies independently of standard pricing`` () =
    let estimate =
        { CostEstimate.Standard
              { Usage.Zero with
                  InputTokens = 1_000_000
                  OutputTokens = 1_000_000 } with
            Tier = PricingTier.Batch }

    let cost = Costing.tryEstimateCostById "claude-opus-4-6" estimate |> Option.get

    Assert.Equal(2_500_000L, cost.InputMicrodollars)
    Assert.Equal(12_500_000L, cost.OutputMicrodollars)
    Assert.Contains("Asynchronous batch pricing", cost.Notes)

[<Fact>]
let ``long-context override is selected at its lower bound`` () =
    let below =
        Costing.tryEstimateCostById
            "gpt-5.4"
            (CostEstimate.Standard
                { Usage.Zero with
                    InputTokens = 272_000 })
        |> Option.get

    let atBoundary =
        Costing.tryEstimateCostById
            "gpt-5.4"
            (CostEstimate.Standard
                { Usage.Zero with
                    InputTokens = 272_001 })
        |> Option.get

    Assert.Equal(9_520_000L, below.InputMicrodollars)
    Assert.Equal(19_040_070L, atBoundary.InputMicrodollars)
    Assert.Empty(below.Notes)
    Assert.Contains(atBoundary.Notes, fun note -> note.Contains("Long-context"))

[<Fact>]
let ``unsupported tier or modality returns None`` () =
    let fast =
        { CostEstimate.Standard Usage.Zero with
            Tier = PricingTier.Fast }

    let audio =
        { CostEstimate.Standard Usage.Zero with
            Modality = PricingModality.Audio }

    Assert.True(Costing.tryEstimateCostById "gpt-5.4" fast |> Option.isNone)
    Assert.True(Costing.tryEstimateCostById "gpt-5.4" audio |> Option.isNone)

[<Fact>]
let ``legacy calculation includes provider cache accounting but local replay remains free`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 1000
            CacheReadTokens = Some 400 }

    let billed = Costing.tryCalculateCostById "gpt-5.4" usage false |> Option.get
    let replayed = Costing.tryCalculateCostById "gpt-5.4" usage true |> Option.get

    Assert.Equal(22_400L, billed.InputMicrodollars)
    Assert.Equal(0L, replayed.TotalMicrodollars)

[<Fact>]
let ``legacy calculation preserves aggregate rounding without provider cache usage`` () =
    let usage =
        { Usage.Zero with
            InputTokens = 2
            OutputTokens = 1 }

    let cost = Costing.tryCalculateCostById "gpt-5-nano" usage false |> Option.get

    Assert.Equal(0L, cost.InputMicrodollars)
    Assert.Equal(0L, cost.OutputMicrodollars)
    Assert.Equal(1L, cost.TotalMicrodollars)
