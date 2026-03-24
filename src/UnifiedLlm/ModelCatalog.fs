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
        { Id = "claude-opus-4-6"; Provider = "anthropic"; DisplayName = "Claude Opus 4.6"
          ContextWindow = 200000; MaxOutput = 32000
          InputCostPerMillion = 15.0; OutputCostPerMillion = 75.0
          Aliases = [ "claude-opus"; "opus-4-6" ]
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
        // OpenAI
        { Id = "gpt-5.2"; Provider = "openai"; DisplayName = "GPT-5.2"
          ContextWindow = 1047576; MaxOutput = 32768
          InputCostPerMillion = 10.0; OutputCostPerMillion = 40.0
          Aliases = [ "gpt-5" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.1-codex-mini"; Provider = "openai"; DisplayName = "GPT-5.1 Codex Mini"
          ContextWindow = 1047576; MaxOutput = 32768
          InputCostPerMillion = 1.5; OutputCostPerMillion = 6.0
          Aliases = [ "codex-mini" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.2-codex"; Provider = "openai"; DisplayName = "GPT-5.2 Codex"
          ContextWindow = 1047576; MaxOutput = 32768
          InputCostPerMillion = 2.0; OutputCostPerMillion = 8.0
          Aliases = [ "gpt-5-codex" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.3-codex"; Provider = "openai"; DisplayName = "GPT-5.3 Codex"
          ContextWindow = 1047576; MaxOutput = 32768
          InputCostPerMillion = 2.5; OutputCostPerMillion = 10.0
          Aliases = [ "gpt-5.3"; "codex-latest" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
        { Id = "gpt-5.4"; Provider = "openai"; DisplayName = "GPT-5.4"
          ContextWindow = 1047576; MaxOutput = 32768
          InputCostPerMillion = 12.0; OutputCostPerMillion = 48.0
          Aliases = [ "gpt-latest" ]
          SupportsStreaming = true; SupportsTools = true
          SupportsReasoning = true; SupportsVision = true }
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
    ]

    let private latestByProvider =
        Map.ofList [
            "anthropic", "claude-opus-4-6"
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
