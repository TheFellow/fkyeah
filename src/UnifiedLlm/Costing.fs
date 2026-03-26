namespace UnifiedLlm

open System
open System.Collections.Concurrent
open System.Threading

type CostBreakdown =
    { Provider: string
      Model: string
      Usage: Usage
      InputMicrodollars: int64
      OutputMicrodollars: int64
      TotalMicrodollars: int64
      CacheHit: bool }
    member this.InputUsd = decimal this.InputMicrodollars / 1_000_000m
    member this.OutputUsd = decimal this.OutputMicrodollars / 1_000_000m
    member this.TotalUsd = decimal this.TotalMicrodollars / 1_000_000m

type CostLedger =
    { Record: CostBreakdown -> unit
      Snapshot: unit -> CostBreakdown list
      TotalMicrodollars: unit -> int64
      TotalUsd: unit -> decimal
      TotalUsage: unit -> Usage
      CallCount: unit -> int
      Summary: unit -> string }

module private Microdollars =

    let fromUsd (value: decimal) =
        Decimal.Round(value * 1_000_000m, 0, MidpointRounding.AwayFromZero)
        |> int64

module CostLedger =

    let inMemory () : CostLedger =
        let calls = ConcurrentBag<CostBreakdown>()
        let mutable totalInputTokens = 0L
        let mutable totalOutputTokens = 0L
        let mutable totalReasoningTokens = 0L
        let mutable totalCacheReadTokens = 0L
        let mutable totalCacheWriteTokens = 0L
        let mutable totalMicros = 0L
        let mutable callCount = 0

        { Record =
            fun cost ->
                calls.Add cost
                Interlocked.Add(&totalInputTokens, int64 cost.Usage.InputTokens) |> ignore
                Interlocked.Add(&totalOutputTokens, int64 cost.Usage.OutputTokens) |> ignore
                Interlocked.Add(&totalReasoningTokens, int64 (cost.Usage.ReasoningTokens |> Option.defaultValue 0)) |> ignore
                Interlocked.Add(&totalCacheReadTokens, int64 (cost.Usage.CacheReadTokens |> Option.defaultValue 0)) |> ignore
                Interlocked.Add(&totalCacheWriteTokens, int64 (cost.Usage.CacheWriteTokens |> Option.defaultValue 0)) |> ignore
                Interlocked.Add(&totalMicros, cost.TotalMicrodollars) |> ignore
                Interlocked.Increment(&callCount) |> ignore
          Snapshot = fun () -> calls |> Seq.toList
          TotalMicrodollars = fun () -> Interlocked.Read(&totalMicros)
          TotalUsd = fun () -> decimal (Interlocked.Read(&totalMicros)) / 1_000_000m
          TotalUsage =
            fun () ->
                { InputTokens = int (Interlocked.Read(&totalInputTokens))
                  OutputTokens = int (Interlocked.Read(&totalOutputTokens))
                  ReasoningTokens = Some (int (Interlocked.Read(&totalReasoningTokens)))
                  CacheReadTokens = Some (int (Interlocked.Read(&totalCacheReadTokens)))
                  CacheWriteTokens = Some (int (Interlocked.Read(&totalCacheWriteTokens))) }
          CallCount = fun () -> Interlocked.CompareExchange(&callCount, 0, 0)
          Summary =
            fun () ->
                let totalUsd = decimal (Interlocked.Read(&totalMicros)) / 1_000_000m
                sprintf
                    "Total: $%.4f (in=%d out=%d, %d calls)"
                    (float totalUsd)
                    (int (Interlocked.Read(&totalInputTokens)))
                    (int (Interlocked.Read(&totalOutputTokens)))
                    (Interlocked.CompareExchange(&callCount, 0, 0)) }

module Costing =

    let calculateCost (model: ModelInfo) (usage: Usage) (cacheHit: bool) : CostBreakdown =
        let inputRate = decimal model.InputCostPerMillion / 1_000_000m
        let outputRate = decimal model.OutputCostPerMillion / 1_000_000m
        let inputUsd =
            if cacheHit then 0m
            else decimal usage.InputTokens * inputRate
        let outputUsd =
            if cacheHit then 0m
            else decimal usage.OutputTokens * outputRate

        { Provider = model.Provider
          Model = model.Id
          Usage = usage
          InputMicrodollars = Microdollars.fromUsd inputUsd
          OutputMicrodollars = Microdollars.fromUsd outputUsd
          TotalMicrodollars = Microdollars.fromUsd (inputUsd + outputUsd)
          CacheHit = cacheHit }

    let tryCalculateCostById (modelId: string) (usage: Usage) (cacheHit: bool) =
        ModelCatalog.tryResolveModel modelId
        |> Option.map (fun model -> calculateCost model usage cacheHit)
