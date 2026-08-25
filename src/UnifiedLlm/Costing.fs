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

[<RequireQualifiedAccess>]
type CacheWriteDuration =
    | FiveMinutes
    | OneHour

type CostEstimate =
    { Usage: Usage
      Tier: PricingTier
      Modality: PricingModality
      CacheWriteDuration: CacheWriteDuration
      CacheStorageMillionTokenHours: decimal
      UnitQuantities: Map<string, decimal> }

    static member Standard(usage: Usage) =
        { Usage = usage
          Tier = PricingTier.Standard
          Modality = PricingModality.Text
          CacheWriteDuration = CacheWriteDuration.FiveMinutes
          CacheStorageMillionTokenHours = 0m
          UnitQuantities = Map.empty }

type UnitCostBreakdown =
    { Unit: string
      Quantity: decimal
      UnitPriceUsd: decimal
      Microdollars: int64
      Notes: string option }

type DetailedCostBreakdown =
    { Provider: string
      Model: string
      Tier: PricingTier
      Modality: PricingModality
      Currency: string
      Usage: Usage
      InputMicrodollars: int64
      CachedInputMicrodollars: int64
      CacheReadMicrodollars: int64
      CacheWriteMicrodollars: int64
      CacheStorageMicrodollars: int64
      OutputMicrodollars: int64
      UnitCosts: UnitCostBreakdown list
      TotalMicrodollars: int64
      Notes: string list }

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
        Decimal.Round(value * 1_000_000m, 0, MidpointRounding.AwayFromZero) |> int64

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

                Interlocked.Add(&totalReasoningTokens, int64 (cost.Usage.ReasoningTokens |> Option.defaultValue 0))
                |> ignore

                Interlocked.Add(&totalCacheReadTokens, int64 (cost.Usage.CacheReadTokens |> Option.defaultValue 0))
                |> ignore

                Interlocked.Add(&totalCacheWriteTokens, int64 (cost.Usage.CacheWriteTokens |> Option.defaultValue 0))
                |> ignore

                Interlocked.Add(&totalMicros, cost.TotalMicrodollars) |> ignore
                Interlocked.Increment(&callCount) |> ignore
          Snapshot = fun () -> calls |> Seq.toList
          TotalMicrodollars = fun () -> Interlocked.Read(&totalMicros)
          TotalUsd = fun () -> decimal (Interlocked.Read(&totalMicros)) / 1_000_000m
          TotalUsage =
            fun () ->
                { InputTokens = int (Interlocked.Read(&totalInputTokens))
                  OutputTokens = int (Interlocked.Read(&totalOutputTokens))
                  ReasoningTokens = Some(int (Interlocked.Read(&totalReasoningTokens)))
                  CacheReadTokens = Some(int (Interlocked.Read(&totalCacheReadTokens)))
                  CacheWriteTokens = Some(int (Interlocked.Read(&totalCacheWriteTokens))) }
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

    let private tokenCost tokenCount ratePerMillion =
        decimal (max 0 tokenCount) * ratePerMillion / 1_000_000m |> Microdollars.fromUsd

    let private appliesTo inputTokens (pricingOverride: PricingOverride) =
        let aboveMinimum =
            pricingOverride.MinInputTokens
            |> Option.forall (fun minimum -> inputTokens >= minimum)

        let belowMaximum =
            pricingOverride.MaxInputTokens
            |> Option.forall (fun maximum -> inputTokens <= maximum)

        aboveMinimum && belowMaximum

    let private resolveModalityPricing
        (estimate: CostEstimate)
        (tierPricing: TierPricing)
        : (ModalityPricing * string list) option =
        let matchingOverride =
            tierPricing.Overrides
            |> List.filter (appliesTo estimate.Usage.InputTokens)
            |> List.tryLast

        match
            matchingOverride
            |> Option.bind (fun value -> value.Modalities |> Map.tryFind estimate.Modality)
        with
        | Some pricing ->
            let notes = matchingOverride |> Option.bind _.Notes |> Option.toList

            Some(pricing, tierPricing.Notes @ notes)
        | None ->
            tierPricing.Modalities
            |> Map.tryFind estimate.Modality
            |> Option.map (fun pricing -> pricing, tierPricing.Notes)

    /// Estimate tier- and modality-aware provider cost. None indicates missing pricing for the requested tier or modality.
    let tryEstimateCost (model: ModelInfo) (estimate: CostEstimate) : DetailedCostBreakdown option =
        ModelCatalog.getPricing model.Id
        |> Option.bind (fun modelPricing ->
            modelPricing.Tiers
            |> Map.tryFind estimate.Tier
            |> Option.bind (resolveModalityPricing estimate)
            |> Option.map (fun (rates, notes) ->
                let usage = estimate.Usage
                let cacheReadTokens = usage.CacheReadTokens |> Option.defaultValue 0 |> max 0
                let cacheWriteTokens = usage.CacheWriteTokens |> Option.defaultValue 0 |> max 0

                let inputTokens, accountingNotes =
                    match modelPricing.InputTokenAccounting with
                    | InputTokenAccounting.ExcludesCacheReads -> max 0 usage.InputTokens, []
                    | InputTokenAccounting.IncludesCacheReads when cacheReadTokens <= usage.InputTokens ->
                        usage.InputTokens - cacheReadTokens, []
                    | InputTokenAccounting.IncludesCacheReads ->
                        max 0 usage.InputTokens,
                        [ "Cache-read tokens exceed input tokens; input token subtraction was skipped" ]

                let inputMicros =
                    rates.InputPerMillion
                    |> Option.map (tokenCost inputTokens)
                    |> Option.defaultValue 0L

                let cachedInputMicros, cacheReadMicros =
                    match rates.CachedInputPerMillion, rates.CacheReadPerMillion with
                    | Some rate, _ -> tokenCost cacheReadTokens rate, 0L
                    | None, Some rate -> 0L, tokenCost cacheReadTokens rate
                    | None, None -> 0L, 0L

                let cacheWriteRate =
                    match estimate.CacheWriteDuration with
                    | CacheWriteDuration.FiveMinutes -> rates.CacheWriteFiveMinutesPerMillion
                    | CacheWriteDuration.OneHour -> rates.CacheWriteOneHourPerMillion

                let cacheWriteMicros =
                    cacheWriteRate
                    |> Option.map (tokenCost cacheWriteTokens)
                    |> Option.defaultValue 0L

                let cacheStorageMicros =
                    rates.CacheStoragePerMillionTokenHour
                    |> Option.map (fun rate ->
                        max 0m estimate.CacheStorageMillionTokenHours * rate |> Microdollars.fromUsd)
                    |> Option.defaultValue 0L

                let outputMicros =
                    rates.OutputPerMillion
                    |> Option.map (tokenCost usage.OutputTokens)
                    |> Option.defaultValue 0L

                let unitCosts =
                    rates.UnitPrices
                    |> List.choose (fun unitPrice ->
                        estimate.UnitQuantities
                        |> Map.tryFind unitPrice.Unit
                        |> Option.filter (fun quantity -> quantity > 0m)
                        |> Option.map (fun quantity ->
                            { Unit = unitPrice.Unit
                              Quantity = quantity
                              UnitPriceUsd = unitPrice.PriceUsd
                              Microdollars = Microdollars.fromUsd (quantity * unitPrice.PriceUsd)
                              Notes = unitPrice.Notes }))

                let unitMicros = unitCosts |> List.sumBy _.Microdollars

                let totalMicros =
                    inputMicros
                    + cachedInputMicros
                    + cacheReadMicros
                    + cacheWriteMicros
                    + cacheStorageMicros
                    + outputMicros
                    + unitMicros

                { Provider = model.Provider
                  Model = model.Id
                  Tier = estimate.Tier
                  Modality = estimate.Modality
                  Currency = modelPricing.Currency
                  Usage = usage
                  InputMicrodollars = inputMicros
                  CachedInputMicrodollars = cachedInputMicros
                  CacheReadMicrodollars = cacheReadMicros
                  CacheWriteMicrodollars = cacheWriteMicros
                  CacheStorageMicrodollars = cacheStorageMicros
                  OutputMicrodollars = outputMicros
                  UnitCosts = unitCosts
                  TotalMicrodollars = totalMicros
                  Notes = notes @ accountingNotes }))

    let tryEstimateCostById (modelId: string) (estimate: CostEstimate) : DetailedCostBreakdown option =
        ModelCatalog.tryResolveModel modelId
        |> Option.bind (fun model -> tryEstimateCost model estimate)

    let calculateCost (model: ModelInfo) (usage: Usage) (cacheHit: bool) : CostBreakdown =
        let hasProviderCacheUsage =
            usage.CacheReadTokens |> Option.exists ((<) 0)
            || usage.CacheWriteTokens |> Option.exists ((<) 0)

        let detailed =
            if not cacheHit && hasProviderCacheUsage then
                tryEstimateCost model (CostEstimate.Standard usage)
            else
                None

        let inputMicros, outputMicros, totalMicros =
            match detailed with
            | Some cost ->
                cost.TotalMicrodollars - cost.OutputMicrodollars, cost.OutputMicrodollars, cost.TotalMicrodollars
            | None when cacheHit -> 0L, 0L, 0L
            | None ->
                // Preserve the original API's rates and aggregate rounding when no provider cache
                // usage is present. Tier, modality and contextual estimates are available via
                // tryEstimateCost/tryEstimateCostById.
                let inputUsd =
                    decimal usage.InputTokens * decimal model.InputCostPerMillion / 1_000_000m

                let outputUsd =
                    decimal usage.OutputTokens * decimal model.OutputCostPerMillion / 1_000_000m

                Microdollars.fromUsd inputUsd,
                Microdollars.fromUsd outputUsd,
                Microdollars.fromUsd (inputUsd + outputUsd)

        { Provider = model.Provider
          Model = model.Id
          Usage = usage
          InputMicrodollars = inputMicros
          OutputMicrodollars = outputMicros
          TotalMicrodollars = totalMicros
          CacheHit = cacheHit }

    let tryCalculateCostById (modelId: string) (usage: Usage) (cacheHit: bool) =
        ModelCatalog.tryResolveModel modelId
        |> Option.map (fun model -> calculateCost model usage cacheHit)
