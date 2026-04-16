namespace UnifiedLlm

/// Information about a known model
type ModelInfo = {
    Id: string
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
    SupportsVision: bool
} with
    member this.SupportsImages = this.SupportsVision

type CapabilityRequirement = {
    RequiresStreaming: bool
    RequiresTools: bool
    RequiresReasoning: bool
    RequiresVision: bool
}

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

/// Built-in catalog of known models
module ModelCatalog =

    let private models : ModelInfo list = [
        // Anthropic
        { Id = "claude-opus-4-7"; Provider = "anthropic"; DisplayName = "Claude Opus 4.7"
          ContextWindow = 200000; MaxOutput = 128000
          InputCostPerMillion = 5.0; OutputCostPerMillion = 25.0
          Aliases = [ "claude-opus"; "opus-4-7"; "latest-anthropic" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-opus-4-6"; Provider = "anthropic"; DisplayName = "Claude Opus 4.6"
          ContextWindow = 200000; MaxOutput = 128000
          InputCostPerMillion = 5.0; OutputCostPerMillion = 25.0
          Aliases = [ "opus-4-6" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-sonnet-4-6"; Provider = "anthropic"; DisplayName = "Claude Sonnet 4.6"
          ContextWindow = 200000; MaxOutput = 64000
          InputCostPerMillion = 3.0; OutputCostPerMillion = 15.0
          Aliases = [ "claude-sonnet"; "sonnet-4-6" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-sonnet-4-5"; Provider = "anthropic"; DisplayName = "Claude Sonnet 4.5"
          ContextWindow = 200000; MaxOutput = 32000
          InputCostPerMillion = 3.0; OutputCostPerMillion = 15.0
          Aliases = [ "sonnet-4-5" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-haiku-4-5"; Provider = "anthropic"; DisplayName = "Claude Haiku 4.5"
          ContextWindow = 200000; MaxOutput = 64000
          InputCostPerMillion = 1.0; OutputCostPerMillion = 5.0
          Aliases = [ "claude-haiku"; "haiku-4-5" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-opus-4-7[1m]"; Provider = "anthropic"; DisplayName = "Claude Opus 4.7 (1M Context)"
          ContextWindow = 1000000; MaxOutput = 128000
          InputCostPerMillion = 5.0; OutputCostPerMillion = 25.0
          Aliases = [ "opus-1m"; "claude-opus-1m" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-opus-4-6[1m]"; Provider = "anthropic"; DisplayName = "Claude Opus 4.6 (1M Context)"
          ContextWindow = 1000000; MaxOutput = 128000
          InputCostPerMillion = 5.0; OutputCostPerMillion = 25.0
          Aliases = [ "opus-4-6-1m" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "claude-sonnet-4-6[1m]"; Provider = "anthropic"; DisplayName = "Claude Sonnet 4.6 (1M Context)"
          ContextWindow = 1000000; MaxOutput = 64000
          InputCostPerMillion = 3.0; OutputCostPerMillion = 15.0
          Aliases = [ "sonnet-1m"; "claude-sonnet-1m" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        // OpenAI
        { Id = "gpt-5.2"; Provider = "openai"; DisplayName = "GPT-5.2"
          ContextWindow = 400000; MaxOutput = 32768
          InputCostPerMillion = 10.0; OutputCostPerMillion = 40.0
          Aliases = [ "gpt-5" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.1-codex-mini"; Provider = "openai"; DisplayName = "GPT-5.1 Codex Mini"
          ContextWindow = 400000; MaxOutput = 32768
          InputCostPerMillion = 1.5; OutputCostPerMillion = 6.0
          Aliases = [ "codex-mini" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.2-codex"; Provider = "openai"; DisplayName = "GPT-5.2 Codex"
          ContextWindow = 400000; MaxOutput = 32768
          InputCostPerMillion = 2.0; OutputCostPerMillion = 8.0
          Aliases = [ "gpt-5-codex" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.3-codex"; Provider = "openai"; DisplayName = "GPT-5.3 Codex"
          ContextWindow = 400000; MaxOutput = 32768
          InputCostPerMillion = 2.5; OutputCostPerMillion = 10.0
          Aliases = [ "gpt-5.3"; "codex-latest" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.4"; Provider = "openai"; DisplayName = "GPT-5.4"
          ContextWindow = 400000; MaxOutput = 128000
          InputCostPerMillion = 35.0; OutputCostPerMillion = 140.0
          Aliases = [ "gpt-latest" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.4-pro"; Provider = "openai"; DisplayName = "GPT-5.4 Pro"
          ContextWindow = 400000; MaxOutput = 128000
          InputCostPerMillion = 70.0; OutputCostPerMillion = 280.0
          Aliases = [ "gpt-5-pro" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5-mini"; Provider = "openai"; DisplayName = "GPT-5 Mini"
          ContextWindow = 400000; MaxOutput = 128000
          InputCostPerMillion = 0.25; OutputCostPerMillion = 2.0
          Aliases = [ "gpt-mini" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5-nano"; Provider = "openai"; DisplayName = "GPT-5 Nano"
          ContextWindow = 400000; MaxOutput = 128000
          InputCostPerMillion = 0.05; OutputCostPerMillion = 0.4
          Aliases = [ "gpt-nano" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-4.1"; Provider = "openai"; DisplayName = "GPT-4.1"
          ContextWindow = 1000000; MaxOutput = 32768
          InputCostPerMillion = 2.0; OutputCostPerMillion = 8.0
          Aliases = [ "gpt-4.1" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = false; SupportsVision = true }
        // Gemini
        { Id = "gemini-3.1-pro-preview"; Provider = "gemini"; DisplayName = "Gemini 3.1 Pro (Preview)"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 7.0; OutputCostPerMillion = 21.0
          Aliases = [ "gemini-3-pro"; "gemini-pro"; "gemini-3.1-pro" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-3-flash-preview"; Provider = "gemini"; DisplayName = "Gemini 3 Flash (Preview)"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 0.35; OutputCostPerMillion = 1.4
          Aliases = [ "gemini-3-flash"; "gemini-flash" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-3.1-pro-preview-customtools"; Provider = "gemini"; DisplayName = "Gemini 3.1 Pro (Preview, Custom Tools)"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 7.0; OutputCostPerMillion = 21.0
          Aliases = [ "gemini-pro-customtools" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-3.1-flash-lite-preview"; Provider = "gemini"; DisplayName = "Gemini 3.1 Flash Lite (Preview)"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 0.075; OutputCostPerMillion = 0.3
          Aliases = [ "gemini-flash-lite"; "gemini-3.1-flash-lite" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-2.5-pro"; Provider = "gemini"; DisplayName = "Gemini 2.5 Pro"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 5.0; OutputCostPerMillion = 15.0
          Aliases = [ "gemini-2.5-pro" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-2.5-flash"; Provider = "gemini"; DisplayName = "Gemini 2.5 Flash"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 0.15; OutputCostPerMillion = 0.6
          Aliases = [ "gemini-2.5-flash" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gemini-2.5-flash-lite"; Provider = "gemini"; DisplayName = "Gemini 2.5 Flash Lite"
          ContextWindow = 1048576; MaxOutput = 65536
          InputCostPerMillion = 0.05; OutputCostPerMillion = 0.2
          Aliases = [ "gemini-2.5-flash-lite" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
    ]

    let private latestByProvider =
        Map.ofList [
            "anthropic", "claude-opus-4-7"
            "openai", "gpt-5.4"
            "gemini", "gemini-3.1-pro-preview"
        ]

    /// Get model info by exact ID. Returns None if unknown.
    let getModelInfo (modelId: string) : ModelInfo option =
        models |> List.tryFind (fun m -> m.Id = modelId)

    /// List all known models
    let listModels () : ModelInfo list =
        models

    /// List models filtered by provider
    let listModelsByProvider (provider: string) : ModelInfo list =
        let normalized = provider.Trim().ToLowerInvariant()
        models |> List.filter (fun m -> m.Provider = normalized)

    /// Return the latest/highest-capability default model for a provider.
    let getLatestModel (provider: string) : ModelInfo option =
        let normalized = provider.Trim().ToLowerInvariant()
        latestByProvider
        |> Map.tryFind normalized
        |> Option.bind getModelInfo

    /// Find the latest provider model satisfying the required capabilities.
    let findModel (provider: string) (required: CapabilityRequirement) : ModelInfo option =
        let normalized = provider.Trim().ToLowerInvariant()
        let candidates =
            models
            |> List.filter (fun model ->
                model.Provider = normalized
                && CapabilityRequirement.satisfiedBy model required)

        match getLatestModel normalized with
        | Some latest when candidates |> List.exists (fun candidate -> candidate.Id = latest.Id) -> Some latest
        | _ -> candidates |> List.tryHead

    /// Find all models satisfying the required capabilities across providers.
    let findModels (required: CapabilityRequirement) : ModelInfo list =
        models
        |> List.filter (fun model -> CapabilityRequirement.satisfiedBy model required)

    /// Resolve a model by exact ID first, then alias.
    let resolveModel (modelId: string) : ModelInfo option =
        getModelInfo modelId
        |> Option.orElseWith (fun () ->
            models
            |> List.tryFind (fun model -> model.Aliases |> List.contains modelId))

    let tryResolveModel (modelId: string) : ModelInfo option =
        resolveModel modelId
