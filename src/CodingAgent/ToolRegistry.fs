namespace CodingAgent

open System.Collections.Generic
open System.Text.Json
open UnifiedLlm

module private JsonSchemaValidation =

    let private jsonTypeName (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> "string"
        | JsonValueKind.Number ->
            let mutable i = 0L
            if value.TryGetInt64(&i) then "integer" else "number"
        | JsonValueKind.True
        | JsonValueKind.False -> "boolean"
        | JsonValueKind.Object -> "object"
        | JsonValueKind.Array -> "array"
        | JsonValueKind.Null -> "null"
        | _ -> "unknown"

    let private getAllowedTypes (schema: JsonElement) : Set<string> option =
        let mutable t = Unchecked.defaultof<JsonElement>
        if not (schema.TryGetProperty("type", &t)) then
            None
        else
            match t.ValueKind with
            | JsonValueKind.String -> Some(set [ t.GetString() ])
            | JsonValueKind.Array ->
                t.EnumerateArray()
                |> Seq.choose (fun item ->
                    if item.ValueKind = JsonValueKind.String then Some(item.GetString())
                    else None)
                |> set
                |> Some
            | _ -> None

    let private valueMatchesTypes (value: JsonElement) (allowedTypes: Set<string>) =
        let actual = jsonTypeName value
        allowedTypes.Contains(actual)
        || (actual = "integer" && allowedTypes.Contains("number"))
        || (actual = "number" && not (allowedTypes.Contains("integer")) && allowedTypes.Contains("number"))

    let private validateRequired (schema: JsonElement) (args: JsonElement) =
        let mutable req = Unchecked.defaultof<JsonElement>
        if not (schema.TryGetProperty("required", &req)) then
            Result.Ok ()
        elif req.ValueKind <> JsonValueKind.Array then
            Result.Error "schema.required must be an array"
        else
            req.EnumerateArray()
            |> Seq.tryPick (fun item ->
                if item.ValueKind <> JsonValueKind.String then
                    Some "schema.required entries must be strings"
                else
                    let name = item.GetString()
                    let mutable value = Unchecked.defaultof<JsonElement>
                    if args.TryGetProperty(name, &value) then None
                    else Some(sprintf "missing required field '%s'" name))
            |> function
                | Some err -> Result.Error err
                | None -> Result.Ok ()

    let private validatePropertyTypes (schema: JsonElement) (args: JsonElement) =
        let mutable props = Unchecked.defaultof<JsonElement>
        if not (schema.TryGetProperty("properties", &props)) then
            Result.Ok ()
        elif props.ValueKind <> JsonValueKind.Object then
            Result.Error "schema.properties must be an object"
        else
            args.EnumerateObject()
            |> Seq.tryPick (fun argProp ->
                let mutable propSchema = Unchecked.defaultof<JsonElement>
                if not (props.TryGetProperty(argProp.Name, &propSchema)) then
                    None
                else
                    match getAllowedTypes propSchema with
                    | None -> None
                    | Some allowed ->
                        if valueMatchesTypes argProp.Value allowed then None
                        else
                            Some(
                                sprintf
                                    "field '%s' expected type %s but got %s"
                                    argProp.Name
                                    (String.concat "|" allowed)
                                    (jsonTypeName argProp.Value)
                            ))
            |> function
                | Some err -> Result.Error err
                | None -> Result.Ok ()

    let validateArguments (schemaJson: string) (argsJson: string) : Result<unit, string> =
        try
            use schemaDoc = JsonDocument.Parse(schemaJson)
            use argsDoc = JsonDocument.Parse(argsJson)
            let schema = schemaDoc.RootElement
            let args = argsDoc.RootElement

            let mutable schemaType = Unchecked.defaultof<JsonElement>
            if schema.TryGetProperty("type", &schemaType)
               && schemaType.ValueKind = JsonValueKind.String
               && schemaType.GetString() = "object"
               && args.ValueKind <> JsonValueKind.Object then
                Result.Error "tool arguments must be a JSON object"
            elif args.ValueKind <> JsonValueKind.Object then
                Result.Error "tool arguments must be a JSON object"
            else
                match validateRequired schema args with
                | Result.Error err -> Result.Error err
                | Result.Ok () -> validatePropertyTypes schema args
        with ex ->
            Result.Error(sprintf "invalid JSON arguments: %s" ex.Message)

/// A registered tool with definition and executor
type RegisteredTool = {
    Definition: ToolDefinition
    IsCacheable: bool
    Execute: (string -> IExecutionEnvironment -> string)
}

/// Registry for dispatching tool calls
type AgentToolRegistry() =
    let tools = Dictionary<string, RegisteredTool>()

    /// Register a tool. Latest-wins for name collisions.
    member _.Register(tool: RegisteredTool) =
        tools.[tool.Definition.Name] <- tool

    /// Unregister a tool by name
    member _.Unregister(name: string) =
        tools.Remove(name) |> ignore

    /// Look up a tool by name
    member _.Resolve(name: string) : RegisteredTool option =
        match tools.TryGetValue(name) with
        | true, tool -> Some tool
        | false, _ -> None

    /// List all tool definitions
    member _.List() : ToolDefinition list =
        tools.Values |> Seq.map (fun t -> t.Definition) |> Seq.toList

    /// List all tool names
    member _.Names() : string list =
        tools.Keys |> Seq.toList

    /// Count of registered tools
    member _.Count = tools.Count

    /// Dispatch a tool call. Returns a ToolResultData.
    /// Unknown tools return error results (not exceptions).
    member _.Dispatch(toolCall: ToolCallData, env: IExecutionEnvironment) : ToolResultData =
        match tools.TryGetValue(toolCall.Name) with
        | false, _ ->
            { ToolCallId = toolCall.Id
              Content = sprintf "Unknown tool: %s" toolCall.Name
              IsError = true
              ImageData = None
              ImageMediaType = None }
        | true, tool ->
            match JsonSchemaValidation.validateArguments tool.Definition.Parameters toolCall.Arguments with
            | Result.Error validationError ->
                { ToolCallId = toolCall.Id
                  Content = sprintf "Tool argument validation failed (%s): %s" toolCall.Name validationError
                  IsError = true
                  ImageData = None
                  ImageMediaType = None }
            | Result.Ok () ->
                try
                    let result = tool.Execute toolCall.Arguments env
                    { ToolCallId = toolCall.Id; Content = result; IsError = false
                      ImageData = None; ImageMediaType = None }
                with ex ->
                    { ToolCallId = toolCall.Id
                      Content = sprintf "Tool error (%s): %s" toolCall.Name ex.Message
                      IsError = true
                      ImageData = None
                      ImageMediaType = None }

    /// Dispatch multiple tool calls (supports parallel if requested)
    member this.DispatchAll(toolCalls: ToolCallData list, env: IExecutionEnvironment, runParallel: bool) : ToolResultData list =
        if runParallel && toolCalls.Length > 1 then
            toolCalls
            |> List.map (fun tc ->
                async {
                    do! Async.SwitchToThreadPool()
                    return this.Dispatch(tc, env)
                })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList
        else
            toolCalls |> List.map (fun tc -> this.Dispatch(tc, env))
