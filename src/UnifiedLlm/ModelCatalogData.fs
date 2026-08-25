namespace UnifiedLlm

/// Information about a known model
type ModelInfo =
    { Id: string
      Provider: string
      DisplayName: string
      ContextWindow: int
      MaxOutput: int
      InputCostPerMillion: float
      OutputCostPerMillion: float
      Aliases: string list
      SupportsStreaming: bool
      SupportsTools: bool
      SupportsReasoning: bool
      SupportsVision: bool }

    member this.SupportsImages = this.SupportsVision

[<RequireQualifiedAccess>]
type PricingTier =
    | Standard
    | Batch
    | Flex
    | Priority
    | Fast

[<RequireQualifiedAccess>]
type PricingModality =
    | Text
    | Audio
    | Image
    | Video

type UnitPrice =
    { Unit: string
      PriceUsd: decimal
      Notes: string option }

type ModalityPricing =
    { InputPerMillion: decimal option
      CachedInputPerMillion: decimal option
      OutputPerMillion: decimal option
      CacheReadPerMillion: decimal option
      CacheWriteFiveMinutesPerMillion: decimal option
      CacheWriteOneHourPerMillion: decimal option
      CacheStoragePerMillionTokenHour: decimal option
      UnitPrices: UnitPrice list }

type PricingOverride =
    { MinInputTokens: int option
      MaxInputTokens: int option
      Modalities: Map<PricingModality, ModalityPricing>
      Notes: string option }

type TierPricing =
    { Modalities: Map<PricingModality, ModalityPricing>
      Overrides: PricingOverride list
      Notes: string list }

[<RequireQualifiedAccess>]
type InputTokenAccounting =
    | IncludesCacheReads
    | ExcludesCacheReads

type ModelPricing =
    { Currency: string
      Unit: string
      InputTokenAccounting: InputTokenAccounting
      Tiers: Map<PricingTier, TierPricing> }

type CapabilityRequirement =
    { RequiresStreaming: bool
      RequiresTools: bool
      RequiresReasoning: bool
      RequiresVision: bool }

module CapabilityRequirement =

    let none =
        { RequiresStreaming = false
          RequiresTools = false
          RequiresReasoning = false
          RequiresVision = false }

    let satisfiedBy (model: ModelInfo) (required: CapabilityRequirement) : bool =
        (not required.RequiresStreaming || model.SupportsStreaming)
        && (not required.RequiresTools || model.SupportsTools)
        && (not required.RequiresReasoning || model.SupportsReasoning)
        && (not required.RequiresVision || model.SupportsVision)

/// Repository-owned built-in model inventory and pricing data.
module internal ModelCatalogData =

    let models: ModelInfo list =
        [
          // Anthropic
          { Id = "claude-opus-4-8"
            Provider = "anthropic"
            DisplayName = "Claude Opus 4.8"
            ContextWindow = 1000000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 25.0
            Aliases =
              [ "claude-opus"
                "opus-4-8"
                "latest-anthropic"
                "opus-1m"
                "claude-opus-1m"
                "claude-opus-4-8[1m]"
                "opus-4-8-1m" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-opus-4-7"
            Provider = "anthropic"
            DisplayName = "Claude Opus 4.7"
            ContextWindow = 200000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 25.0
            Aliases = [ "opus-4-7" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-opus-4-6"
            Provider = "anthropic"
            DisplayName = "Claude Opus 4.6"
            ContextWindow = 200000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 25.0
            Aliases = [ "opus-4-6" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-sonnet-5"
            Provider = "anthropic"
            DisplayName = "Claude Sonnet 5"
            ContextWindow = 1000000
            MaxOutput = 128000
            InputCostPerMillion = 3.0
            OutputCostPerMillion = 15.0
            Aliases =
              [ "claude-sonnet"
                "sonnet-5"
                "claude-sonnet-latest"
                "sonnet-latest"
                "latest-anthropic-sonnet"
                "sonnet-1m"
                "claude-sonnet-1m" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-sonnet-4-6"
            Provider = "anthropic"
            DisplayName = "Claude Sonnet 4.6"
            ContextWindow = 200000
            MaxOutput = 64000
            InputCostPerMillion = 3.0
            OutputCostPerMillion = 15.0
            Aliases = [ "sonnet-4-6" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-sonnet-4-5"
            Provider = "anthropic"
            DisplayName = "Claude Sonnet 4.5"
            ContextWindow = 200000
            MaxOutput = 32000
            InputCostPerMillion = 3.0
            OutputCostPerMillion = 15.0
            Aliases = [ "sonnet-4-5" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-haiku-4-5"
            Provider = "anthropic"
            DisplayName = "Claude Haiku 4.5"
            ContextWindow = 200000
            MaxOutput = 64000
            InputCostPerMillion = 1.0
            OutputCostPerMillion = 5.0
            Aliases = [ "claude-haiku"; "haiku-4-5" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-opus-4-7[1m]"
            Provider = "anthropic"
            DisplayName = "Claude Opus 4.7 (1M Context)"
            ContextWindow = 1000000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 25.0
            Aliases = [ "opus-1m"; "claude-opus-1m" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-opus-4-6[1m]"
            Provider = "anthropic"
            DisplayName = "Claude Opus 4.6 (1M Context)"
            ContextWindow = 1000000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 25.0
            Aliases = [ "opus-4-6-1m" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "claude-sonnet-4-6[1m]"
            Provider = "anthropic"
            DisplayName = "Claude Sonnet 4.6 (1M Context)"
            ContextWindow = 1000000
            MaxOutput = 64000
            InputCostPerMillion = 3.0
            OutputCostPerMillion = 15.0
            Aliases = [ "sonnet-1m"; "claude-sonnet-1m" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          // OpenAI
          { Id = "gpt-5.2"
            Provider = "openai"
            DisplayName = "GPT-5.2"
            ContextWindow = 400000
            MaxOutput = 32768
            InputCostPerMillion = 10.0
            OutputCostPerMillion = 40.0
            Aliases = [ "gpt-5" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.1-codex-mini"
            Provider = "openai"
            DisplayName = "GPT-5.1 Codex Mini"
            ContextWindow = 400000
            MaxOutput = 32768
            InputCostPerMillion = 1.5
            OutputCostPerMillion = 6.0
            Aliases = [ "codex-mini" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.2-codex"
            Provider = "openai"
            DisplayName = "GPT-5.2 Codex"
            ContextWindow = 400000
            MaxOutput = 32768
            InputCostPerMillion = 2.0
            OutputCostPerMillion = 8.0
            Aliases = [ "gpt-5-codex" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.3-codex"
            Provider = "openai"
            DisplayName = "GPT-5.3 Codex"
            ContextWindow = 400000
            MaxOutput = 32768
            InputCostPerMillion = 2.5
            OutputCostPerMillion = 10.0
            Aliases = [ "gpt-5.3"; "codex-latest" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.4"
            Provider = "openai"
            DisplayName = "GPT-5.4"
            ContextWindow = 400000
            MaxOutput = 128000
            InputCostPerMillion = 35.0
            OutputCostPerMillion = 140.0
            Aliases = [ "gpt-5.4" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.5"
            Provider = "openai"
            DisplayName = "GPT-5.5"
            ContextWindow = 1000000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 30.0
            Aliases = [ "gpt-5.5" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.6-sol"
            Provider = "openai"
            DisplayName = "GPT-5.6 Sol"
            ContextWindow = 1050000
            MaxOutput = 128000
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 30.0
            Aliases = [ "gpt-5.6"; "gpt-latest" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.6-terra"
            Provider = "openai"
            DisplayName = "GPT-5.6 Terra"
            ContextWindow = 1050000
            MaxOutput = 128000
            InputCostPerMillion = 2.5
            OutputCostPerMillion = 15.0
            Aliases = [ "gpt-5.6-terra" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.6-luna"
            Provider = "openai"
            DisplayName = "GPT-5.6 Luna"
            ContextWindow = 1050000
            MaxOutput = 128000
            InputCostPerMillion = 1.0
            OutputCostPerMillion = 6.0
            Aliases = [ "gpt-5.6-luna" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5.4-pro"
            Provider = "openai"
            DisplayName = "GPT-5.4 Pro"
            ContextWindow = 400000
            MaxOutput = 128000
            InputCostPerMillion = 70.0
            OutputCostPerMillion = 280.0
            Aliases = [ "gpt-5-pro" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5-mini"
            Provider = "openai"
            DisplayName = "GPT-5 Mini"
            ContextWindow = 400000
            MaxOutput = 128000
            InputCostPerMillion = 0.25
            OutputCostPerMillion = 2.0
            Aliases = [ "gpt-mini" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-5-nano"
            Provider = "openai"
            DisplayName = "GPT-5 Nano"
            ContextWindow = 400000
            MaxOutput = 128000
            InputCostPerMillion = 0.05
            OutputCostPerMillion = 0.4
            Aliases = [ "gpt-nano" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gpt-4.1"
            Provider = "openai"
            DisplayName = "GPT-4.1"
            ContextWindow = 1000000
            MaxOutput = 32768
            InputCostPerMillion = 2.0
            OutputCostPerMillion = 8.0
            Aliases = [ "gpt-4.1" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = false
            SupportsVision = true }
          // Gemini
          { Id = "gemini-3.1-pro-preview"
            Provider = "gemini"
            DisplayName = "Gemini 3.1 Pro (Preview)"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 7.0
            OutputCostPerMillion = 21.0
            Aliases = [ "gemini-3-pro"; "gemini-pro"; "gemini-3.1-pro" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-3-flash-preview"
            Provider = "gemini"
            DisplayName = "Gemini 3 Flash (Preview)"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 0.35
            OutputCostPerMillion = 1.4
            Aliases = [ "gemini-3-flash"; "gemini-flash" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-3.1-pro-preview-customtools"
            Provider = "gemini"
            DisplayName = "Gemini 3.1 Pro (Preview, Custom Tools)"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 7.0
            OutputCostPerMillion = 21.0
            Aliases = [ "gemini-pro-customtools" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-3.1-flash-lite-preview"
            Provider = "gemini"
            DisplayName = "Gemini 3.1 Flash Lite (Preview)"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 0.075
            OutputCostPerMillion = 0.3
            Aliases = [ "gemini-flash-lite"; "gemini-3.1-flash-lite" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-2.5-pro"
            Provider = "gemini"
            DisplayName = "Gemini 2.5 Pro"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 5.0
            OutputCostPerMillion = 15.0
            Aliases = [ "gemini-2.5-pro" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-2.5-flash"
            Provider = "gemini"
            DisplayName = "Gemini 2.5 Flash"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 0.15
            OutputCostPerMillion = 0.6
            Aliases = [ "gemini-2.5-flash" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true }
          { Id = "gemini-2.5-flash-lite"
            Provider = "gemini"
            DisplayName = "Gemini 2.5 Flash Lite"
            ContextWindow = 1048576
            MaxOutput = 65536
            InputCostPerMillion = 0.05
            OutputCostPerMillion = 0.2
            Aliases = [ "gemini-2.5-flash-lite" ]
            SupportsStreaming = true
            SupportsTools = true
            SupportsReasoning = true
            SupportsVision = true } ]

    let latestByProvider =
        Map.ofList
            [ "anthropic", "claude-opus-4-8"
              "openai", "gpt-5.6-sol"
              "gemini", "gemini-3.1-pro-preview" ]

    let private textPricing (model: ModelInfo) cachedInput cacheRead cacheWriteFiveMinutes cacheWriteOneHour =
        { InputPerMillion = Some(decimal model.InputCostPerMillion)
          CachedInputPerMillion = cachedInput
          OutputPerMillion = Some(decimal model.OutputCostPerMillion)
          CacheReadPerMillion = cacheRead
          CacheWriteFiveMinutesPerMillion = cacheWriteFiveMinutes
          CacheWriteOneHourPerMillion = cacheWriteOneHour
          CacheStoragePerMillionTokenHour = None
          UnitPrices = [] }

    let private discounted (factor: decimal) (pricing: ModalityPricing) =
        let multiply = Option.map ((*) factor)

        { pricing with
            InputPerMillion = multiply pricing.InputPerMillion
            CachedInputPerMillion = multiply pricing.CachedInputPerMillion
            OutputPerMillion = multiply pricing.OutputPerMillion
            CacheReadPerMillion = multiply pricing.CacheReadPerMillion
            CacheWriteFiveMinutesPerMillion = multiply pricing.CacheWriteFiveMinutesPerMillion
            CacheWriteOneHourPerMillion = multiply pricing.CacheWriteOneHourPerMillion }

    let private tier pricing overrides notes =
        { Modalities = Map.ofList [ PricingModality.Text, pricing ]
          Overrides = overrides
          Notes = notes }

    let private defaultPricing (model: ModelInfo) =
        let input = decimal model.InputCostPerMillion

        let accounting, standard =
            match model.Provider with
            | "anthropic" ->
                InputTokenAccounting.ExcludesCacheReads,
                textPricing model None (Some(input * 0.1m)) (Some(input * 1.25m)) (Some(input * 2m))
            | _ -> InputTokenAccounting.IncludesCacheReads, textPricing model (Some(input * 0.1m)) None None None

        let longContextOverrides =
            if model.Id = "gpt-5.4" then
                let longContext =
                    { standard with
                        InputPerMillion = standard.InputPerMillion |> Option.map ((*) 2m)
                        CachedInputPerMillion = standard.CachedInputPerMillion |> Option.map ((*) 2m)
                        OutputPerMillion = standard.OutputPerMillion |> Option.map ((*) 1.5m) }

                [ { MinInputTokens = Some 272_001
                    MaxInputTokens = None
                    Modalities = Map.ofList [ PricingModality.Text, longContext ]
                    Notes = Some "Long-context pricing above 272k input tokens" } ]
            else
                []

        { Currency = "USD"
          Unit = "per_1m_tokens"
          InputTokenAccounting = accounting
          Tiers =
            Map.ofList
                [ PricingTier.Standard, tier standard longContextOverrides []
                  PricingTier.Batch, tier (discounted 0.5m standard) [] [ "Asynchronous batch pricing" ] ] }

    let pricingByModel: Map<string, ModelPricing> =
        models |> List.map (fun model -> model.Id, defaultPricing model) |> Map.ofList
