namespace UnifiedLlm

/// Built-in catalog of known models.
module ModelCatalog =

    /// Get model info by exact ID. Returns None if unknown.
    let getModelInfo (modelId: string) : ModelInfo option =
        ModelCatalogData.models |> List.tryFind (fun model -> model.Id = modelId)

    /// List all known models.
    let listModels () : ModelInfo list = ModelCatalogData.models

    /// List models filtered by provider.
    let listModelsByProvider (provider: string) : ModelInfo list =
        let normalized = provider.Trim().ToLowerInvariant()

        ModelCatalogData.models
        |> List.filter (fun model -> model.Provider = normalized)

    /// Return pricing metadata by exact model ID.
    let getPricing (modelId: string) : ModelPricing option =
        ModelCatalogData.pricingByModel |> Map.tryFind modelId

    /// Return the latest/highest-capability default model for a provider.
    let getLatestModel (provider: string) : ModelInfo option =
        let normalized = provider.Trim().ToLowerInvariant()

        ModelCatalogData.latestByProvider
        |> Map.tryFind normalized
        |> Option.bind getModelInfo

    /// Find the latest provider model satisfying the required capabilities.
    let findModel (provider: string) (required: CapabilityRequirement) : ModelInfo option =
        let normalized = provider.Trim().ToLowerInvariant()

        let candidates =
            ModelCatalogData.models
            |> List.filter (fun model ->
                model.Provider = normalized && CapabilityRequirement.satisfiedBy model required)

        match getLatestModel normalized with
        | Some latest when candidates |> List.exists (fun candidate -> candidate.Id = latest.Id) -> Some latest
        | _ -> candidates |> List.tryHead

    /// Find all models satisfying the required capabilities across providers.
    let findModels (required: CapabilityRequirement) : ModelInfo list =
        ModelCatalogData.models
        |> List.filter (fun model -> CapabilityRequirement.satisfiedBy model required)

    /// Resolve a model by exact ID first, then alias.
    let resolveModel (modelId: string) : ModelInfo option =
        getModelInfo modelId
        |> Option.orElseWith (fun () ->
            ModelCatalogData.models
            |> List.tryFind (fun model -> model.Aliases |> List.contains modelId))

    let tryResolveModel (modelId: string) : ModelInfo option = resolveModel modelId

    /// Resolve pricing by exact model ID or alias.
    let resolvePricing (modelId: string) : ModelPricing option =
        resolveModel modelId |> Option.bind (fun model -> getPricing model.Id)
