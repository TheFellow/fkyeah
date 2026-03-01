namespace UnifiedLlm

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// Result of a high-level generate call
type GenerateResult = {
    Text: string
    Reasoning: string option
    ToolCalls: ToolCallData list
    ToolResults: ToolResultData list
    FinishReason: FinishReason
    Usage: Usage
    TotalUsage: Usage
    Steps: StepResult list
    Response: Response
}

/// High-level generation functions
module Generation =

    type private RequestOptions = {
        MaxRetries: int option
        Timeout: TimeSpan option
        TimeoutConfig: TimeoutConfig option
        AdapterTimeout: AdapterTimeout option
        AbortSignal: AbortSignal option
        Temperature: float option
        TopP: float option
        StopSequences: string list option
        ResponseFormat: ResponseFormat option
        Metadata: Map<string, string> option
        StopWhen: StopCondition list option
    }

    let private defaultRequestOptions = {
        MaxRetries = None
        Timeout = None
        TimeoutConfig = None
        AdapterTimeout = None
        AbortSignal = None
        Temperature = None
        TopP = None
        StopSequences = None
        ResponseFormat = None
        Metadata = None
        StopWhen = None
    }

    type private SeqAsyncEnumerable<'T>(source: seq<'T>) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_cancellationToken: CancellationToken) =
                let enumerator = source.GetEnumerator()
                { new IAsyncEnumerator<'T> with
                    member _.Current = enumerator.Current
                    member _.MoveNextAsync() = ValueTask<bool>(enumerator.MoveNext())
                    member _.DisposeAsync() =
                        enumerator.Dispose()
                        ValueTask() }

    let private toAsyncEnumerable (source: seq<'T>) : IAsyncEnumerable<'T> =
        SeqAsyncEnumerable(source) :> IAsyncEnumerable<'T>

    type StreamAccumulator(stream: IAsyncEnumerable<StreamEvent>, ?model: string, ?provider: string) =
        let text = System.Text.StringBuilder()
        let toolCalls = Dictionary<string, ToolCallData>()
        let toolOrder = ResizeArray<string>()
        let mutable usage = Usage.Zero
        let mutable finishReason = Stop "streaming"
        let mutable currentModel = defaultArg model ""
        let mutable currentProvider = defaultArg provider ""
        let mutable responseId: string option = None
        let mutable finalized: Response option = None

        let snapshot () =
            match finalized with
            | Some response -> response
            | None ->
                let contentParts =
                    [
                        if text.Length > 0 then
                            yield Text(text.ToString())
                        for id in toolOrder do
                            if toolCalls.ContainsKey(id) then
                                yield ToolCall(toolCalls[id])
                    ]

                { Id = responseId |> Option.defaultValue ("stream-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                  Model = currentModel
                  Provider = currentProvider
                  Message =
                    { Role = Assistant
                      Content = if contentParts.IsEmpty then [ Text "" ] else contentParts
                      Name = None
                      ToolCallId = None }
                  FinishReason = finishReason
                  Usage = usage
                  ResponseId = responseId
                  Raw = None
                  Warnings = []
                  RateLimit = None }

        let processEvent (evt: StreamEvent) =
            match evt with
            | StreamStart
            | TextStart _
            | TextEnd _
            | ReasoningStart _
            | ReasoningEnd _
            | ThinkingEvent _
            | ProviderEvent _
            | StreamError _ -> ()
            | TextDelta(_, delta) ->
                text.Append(delta) |> ignore
            | ToolCallStart tc ->
                if not (toolCalls.ContainsKey(tc.Id)) then
                    toolOrder.Add(tc.Id)
                toolCalls[tc.Id] <- tc
            | ToolCallDelta(id, argsDelta) ->
                if toolCalls.ContainsKey(id) then
                    let existing = toolCalls[id]
                    toolCalls[id] <- { existing with Arguments = existing.Arguments + argsDelta }
                else
                    let tc = { Id = id; Name = "unknown_tool"; Arguments = argsDelta; Metadata = Map.empty }
                    toolCalls[id] <- tc
                    toolOrder.Add(id)
            | ToolCallEnd tc ->
                if not (toolCalls.ContainsKey(tc.Id)) then
                    toolOrder.Add(tc.Id)
                toolCalls[tc.Id] <- tc
            | StepFinish(_, responseOpt) ->
                match responseOpt with
                | Some response ->
                    responseId <- response.ResponseId
                    currentModel <- response.Model
                    currentProvider <- response.Provider
                    usage <- response.Usage
                | None -> ()
            | Finish(reason, usageOpt, responseOpt) ->
                finishReason <- reason
                match usageOpt with
                | Some u -> usage <- u
                | None -> ()
                match responseOpt with
                | Some response ->
                    responseId <- response.ResponseId
                    currentModel <- response.Model
                    currentProvider <- response.Provider
                    usage <- response.Usage
                    finalized <- Some response
                | None ->
                    finalized <- Some(snapshot ())

        member _.Events : IAsyncEnumerable<StreamEvent> =
            { new IAsyncEnumerable<StreamEvent> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    let inner = stream.GetAsyncEnumerator(cancellationToken)
                    { new IAsyncEnumerator<StreamEvent> with
                        member _.Current = inner.Current
                        member _.MoveNextAsync() =
                            ValueTask<bool>(
                                task {
                                    let! hasNext = inner.MoveNextAsync().AsTask()
                                    if hasNext then
                                        processEvent inner.Current
                                    return hasNext
                                })
                        member _.DisposeAsync() = inner.DisposeAsync() } }

        member _.TextStream : IAsyncEnumerable<string> =
            { new IAsyncEnumerable<string> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    let inner = stream.GetAsyncEnumerator(cancellationToken)
                    let mutable current = ""

                    let rec moveNextText () =
                        task {
                            let! hasNext = inner.MoveNextAsync().AsTask()
                            if not hasNext then
                                return false
                            else
                                let evt = inner.Current
                                processEvent evt
                                match evt with
                                | TextDelta(_, delta) ->
                                    current <- delta
                                    return true
                                | _ ->
                                    return! moveNextText ()
                        }

                    { new IAsyncEnumerator<string> with
                        member _.Current = current
                        member _.MoveNextAsync() = ValueTask<bool>(moveNextText ())
                        member _.DisposeAsync() = inner.DisposeAsync() } }

        member _.PartialResponse() =
            snapshot ()

    type StreamObjectResult<'T> = {
        PartialObjects: IAsyncEnumerable<'T>
        FinalObject: unit -> 'T option
    }

    let private isToolCalls (reason: FinishReason) =
        match reason with
        | ToolCalls _ -> true
        | _ -> false

    let private maxRetriesConfig (maxRetries: int option) =
        { RetryConfig.Default with
            MaxRetries = maxRetries |> Option.defaultValue RetryConfig.Default.MaxRetries }

    let private throwIfAborted (abortSignal: AbortSignal option) =
        match abortSignal with
        | Some signal when signal.IsAborted ->
            raise (AbortError("Request aborted by caller"))
        | _ -> ()

    let private matchesStopCondition (condition: StopCondition) (response: Response) (roundsExecuted: int) =
        match condition with
        | ToolCalled toolName ->
            response.ToolCalls |> List.exists (fun tc -> tc.Name = toolName)
        | TextMatches pattern ->
            if String.IsNullOrWhiteSpace(pattern) then
                false
            else
                try Regex.IsMatch(response.Text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.Multiline)
                with _ -> response.Text.Contains(pattern, StringComparison.OrdinalIgnoreCase)
        | MaxRounds n ->
            n > 0 && roundsExecuted >= n

    let private shouldStop (stopWhen: StopCondition list option) (response: Response) (roundsExecuted: int) =
        stopWhen
        |> Option.defaultValue []
        |> List.exists (fun condition -> matchesStopCondition condition response roundsExecuted)

    /// Validate that prompt and messages are mutually exclusive
    let validateInput (prompt: string option) (messages: Message list option) =
        match prompt, messages with
        | Some _, Some _ -> raise (ValidationError("Cannot provide both 'prompt' and 'messages'"))
        | None, None -> raise (ValidationError("Must provide either 'prompt' or 'messages'"))
        | _ -> ()

    /// Build messages from prompt or messages input
    let buildMessages (prompt: string option) (messages: Message list option) (system: string option) : Message list =
        validateInput prompt messages
        let baseMessages =
            match prompt with
            | Some p -> [ Message.user(p) ]
            | Option.None ->
                match messages with
                | Some m -> m
                | Option.None -> []
        match system with
        | Some s -> Message.system(s) :: baseMessages
        | Option.None -> baseMessages

    let private tryParseJsonDocument (json: string) =
        try
            Some (JsonDocument.Parse(json))
        with _ ->
            None

    let private jsonTypeMatches (expectedType: string) (value: JsonElement) =
        match expectedType, value.ValueKind with
        | "string", JsonValueKind.String -> true
        | "integer", JsonValueKind.Number ->
            let mutable ignored = 0
            value.TryGetInt32(&ignored)
        | "number", JsonValueKind.Number -> true
        | "boolean", JsonValueKind.True
        | "boolean", JsonValueKind.False -> true
        | "object", JsonValueKind.Object -> true
        | "array", JsonValueKind.Array -> true
        | "null", JsonValueKind.Null -> true
        | _ -> false

    let private validateToolArguments (tool: Tool) (argumentsJson: string) : string list =
        let schemaOpt = tryParseJsonDocument tool.Definition.Parameters
        let argsOpt = tryParseJsonDocument argumentsJson

        match schemaOpt, argsOpt with
        | _, None -> [ "Tool arguments are not valid JSON" ]
        | None, Some _ -> []
        | Some schemaDoc, Some argsDoc ->
            let schema = schemaDoc.RootElement
            let args = argsDoc.RootElement

            if args.ValueKind <> JsonValueKind.Object then
                [ "Tool arguments must be a JSON object" ]
            else
                let required =
                    if schema.TryGetProperty("required") |> fst then
                        schema.GetProperty("required").EnumerateArray()
                        |> Seq.choose (fun e -> if e.ValueKind = JsonValueKind.String then Some(e.GetString()) else None)
                        |> Seq.toList
                    else []

                let properties =
                    if schema.TryGetProperty("properties") |> fst then
                        schema.GetProperty("properties")
                    else
                        JsonDocument.Parse("{}").RootElement

                let missingErrors =
                    required
                    |> List.choose (fun name ->
                        if args.TryGetProperty(name) |> fst then None
                        else Some(sprintf "Missing required field '%s'" name))

                let typeErrors =
                    properties.EnumerateObject()
                    |> Seq.choose (fun p ->
                        let propName = p.Name
                        let propSchema = p.Value
                        if not (args.TryGetProperty(propName) |> fst) then None
                        elif not (propSchema.TryGetProperty("type") |> fst) then None
                        else
                            let expectedType = propSchema.GetProperty("type").GetString()
                            let argValue = args.GetProperty(propName)
                            if jsonTypeMatches expectedType argValue then None
                            else Some(sprintf "Field '%s' must be type '%s'" propName expectedType))
                    |> Seq.toList

                missingErrors @ typeErrors

    /// Execute a tool call against a tool registry
    let executeTool (tools: Tool list) (toolCall: ToolCallData) : ToolResultData =
        let tool = tools |> List.tryFind (fun t -> t.Definition.Name = toolCall.Name)
        match tool with
        | Option.None ->
            { ToolCallId = toolCall.Id; Content = sprintf "Unknown tool: %s" toolCall.Name; IsError = true
              ImageData = None; ImageMediaType = None }
        | Some t ->
            let validationErrors = validateToolArguments t toolCall.Arguments
            if not validationErrors.IsEmpty then
                { ToolCallId = toolCall.Id
                  Content = sprintf "Tool argument validation failed: %s" (String.concat "; " validationErrors)
                  IsError = true
                  ImageData = None
                  ImageMediaType = None }
            else
                match t.Execute with
                | Option.None ->
                    { ToolCallId = toolCall.Id; Content = "Tool has no execute handler"; IsError = true
                      ImageData = None; ImageMediaType = None }
                | Some exec ->
                    try
                        let result = exec toolCall.Arguments
                        { ToolCallId = toolCall.Id; Content = result; IsError = false
                          ImageData = None; ImageMediaType = None }
                    with ex ->
                        { ToolCallId = toolCall.Id; Content = sprintf "Tool error: %s" ex.Message; IsError = true
                          ImageData = None; ImageMediaType = None }

    /// Execute multiple tool calls concurrently while preserving order.
    let executeAllTools (tools: Tool list) (toolCalls: ToolCallData list) : ToolResultData list =
        toolCalls
        |> List.map (fun tc -> async { return executeTool tools tc })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    let private completeWithRetry (client: Client) (request: Request) (maxRetries: int option) =
        Retry.execute (maxRetriesConfig maxRetries) (fun () ->
            throwIfAborted request.AbortSignal
            client.Complete(request))

    let private buildRequest
        (model: string)
        (conversation: Message list)
        (toolDefs: ToolDefinition list option)
        (provider: string option)
        (reasoningEffort: string option)
        (options: RequestOptions)
        =
        let requestTimeout =
            options.Timeout
            |> Option.orElseWith (fun () ->
                options.TimeoutConfig
                |> Option.bind (fun timeout ->
                    timeout.PerStepMs
                    |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))))

        { Request.Create(model, conversation) with
            Tools = toolDefs
            ToolChoice = if toolDefs.IsSome then Some ToolChoice.Auto else None
            Provider = provider
            ReasoningEffort = reasoningEffort
            Timeout = requestTimeout
            TimeoutConfig = options.TimeoutConfig
            AdapterTimeout = options.AdapterTimeout
            AbortSignal = options.AbortSignal
            Temperature = options.Temperature
            TopP = options.TopP
            StopSequences = options.StopSequences
            ResponseFormat = options.ResponseFormat
            Metadata = options.Metadata }

    let private generateInternal
        (client: Client)
        (model: string)
        (prompt: string option)
        (messages: Message list option)
        (system: string option)
        (tools: Tool list option)
        (maxToolRounds: int)
        (provider: string option)
        (reasoningEffort: string option)
        (options: RequestOptions)
        : GenerateResult =

        let initialMessages = buildMessages prompt messages system
        let toolDefs =
            tools |> Option.map (List.map (fun t -> t.Definition))

        let mutable conversation = initialMessages
        let mutable steps: StepResult list = []
        let mutable totalUsage = Usage.Zero
        let mutable roundCount = 0
        let mutable keepLooping = true
        let startedAt = DateTimeOffset.UtcNow

        while keepLooping do
            match options.TimeoutConfig with
            | Some timeoutConfig ->
                match timeoutConfig.TotalMs with
                | Some totalMs when totalMs > 0 ->
                    let elapsedMs = int (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                    if elapsedMs > totalMs then
                        raise (RequestTimeoutError(sprintf "Generation exceeded total timeout (%dms)" totalMs))
                | _ -> ()
            | None -> ()

            let request =
                buildRequest model conversation toolDefs provider reasoningEffort options

            let response = completeWithRetry client request options.MaxRetries
            let toolCalls = response.ToolCalls
            totalUsage <- totalUsage + response.Usage

            let hasActiveTools =
                match tools with
                | Some ts -> ts |> List.exists (fun t -> t.Execute.IsSome)
                | None -> false

            let toolResults =
                match tools with
                | Some ts when not toolCalls.IsEmpty && isToolCalls response.FinishReason && hasActiveTools ->
                    executeAllTools ts toolCalls
                | _ -> []

            let step = {
                ToolCalls = toolCalls
                ToolResults = toolResults
                Usage = response.Usage
                Response = response
            }
            steps <- steps @ [ step ]

            let continuingWithTools =
                not toolCalls.IsEmpty
                && isToolCalls response.FinishReason
                && hasActiveTools
            let roundsAfterThisStep =
                if continuingWithTools then roundCount + 1 else roundCount
            let stopConditionMatched =
                shouldStop options.StopWhen response roundsAfterThisStep

            if toolCalls.IsEmpty || not (isToolCalls response.FinishReason) then
                keepLooping <- false
            elif not hasActiveTools then
                keepLooping <- false
            elif roundCount >= maxToolRounds then
                keepLooping <- false
            elif stopConditionMatched then
                keepLooping <- false
            else
                roundCount <- roundsAfterThisStep
                conversation <- conversation @ [ response.Message ]
                for result in toolResults do
                    conversation <- conversation @ [ Message.toolResult(result.ToolCallId, result.Content, result.IsError) ]

        let lastStep = steps |> List.last
        {
            Text = lastStep.Response.Text
            Reasoning = lastStep.Response.Reasoning
            ToolCalls = lastStep.ToolCalls
            ToolResults = lastStep.ToolResults
            FinishReason = lastStep.Response.FinishReason
            Usage = lastStep.Usage
            TotalUsage = totalUsage
            Steps = steps
            Response = lastStep.Response
        }

    /// High-level generate function with automatic tool loop and advanced controls.
    let generateWithControl
        (client: Client)
        (model: string)
        (prompt: string option)
        (messages: Message list option)
        (system: string option)
        (tools: Tool list option)
        (maxToolRounds: int)
        (provider: string option)
        (reasoningEffort: string option)
        (maxRetries: int option)
        (timeout: TimeSpan option)
        (abortSignal: AbortSignal option)
        (stopWhen: StopCondition list option)
        (timeoutConfig: TimeoutConfig option)
        (adapterTimeout: AdapterTimeout option)
        : GenerateResult =

        let options =
            { defaultRequestOptions with
                MaxRetries = maxRetries
                Timeout = timeout
                TimeoutConfig = timeoutConfig
                AdapterTimeout = adapterTimeout
                AbortSignal = abortSignal
                StopWhen = stopWhen }

        generateInternal client model prompt messages system tools maxToolRounds provider reasoningEffort options

    /// Backward-compatible generate function.
    let generate
        (client: Client)
        (model: string)
        (prompt: string option)
        (messages: Message list option)
        (system: string option)
        (tools: Tool list option)
        (maxToolRounds: int)
        (provider: string option)
        (reasoningEffort: string option)
        (stopWhen: StopCondition list option)
        : GenerateResult =
        generateWithControl
            client
            model
            prompt
            messages
            system
            tools
            maxToolRounds
            provider
            reasoningEffort
            None
            None
            None
            stopWhen
            None
            None

    let private validateJsonAgainstSchema (schemaText: string) (jsonText: string) =
        use schemaDoc = JsonDocument.Parse(schemaText)
        use payloadDoc = JsonDocument.Parse(jsonText)

        let schema = schemaDoc.RootElement
        let payload = payloadDoc.RootElement

        if schema.TryGetProperty("type") |> fst then
            let expected = schema.GetProperty("type").GetString()
            if not (jsonTypeMatches expected payload) then
                raise (NoObjectGeneratedError(sprintf "Generated JSON type mismatch. Expected '%s'." expected))

        if payload.ValueKind = JsonValueKind.Object then
            let required =
                if schema.TryGetProperty("required") |> fst then
                    schema.GetProperty("required").EnumerateArray()
                    |> Seq.choose (fun e -> if e.ValueKind = JsonValueKind.String then Some(e.GetString()) else None)
                    |> Seq.toList
                else []

            for field in required do
                if not (payload.TryGetProperty(field) |> fst) then
                    raise (NoObjectGeneratedError(sprintf "Generated JSON missing required field '%s'." field))

            if schema.TryGetProperty("properties") |> fst then
                let properties = schema.GetProperty("properties")
                for p in properties.EnumerateObject() do
                    if payload.TryGetProperty(p.Name) |> fst then
                        let value = payload.GetProperty(p.Name)
                        if p.Value.TryGetProperty("type") |> fst then
                            let expected = p.Value.GetProperty("type").GetString()
                            if not (jsonTypeMatches expected value) then
                                raise (NoObjectGeneratedError(sprintf "Generated JSON field '%s' should be '%s'." p.Name expected))

    /// Generate structured JSON using provider-native response format controls.
    let generateObjectWithControl
        (client: Client)
        (model: string)
        (prompt: string)
        (schema: string)
        (provider: string option)
        (maxRetries: int option)
        : GenerateResult =

        let options =
            { defaultRequestOptions with
                MaxRetries = maxRetries
                ResponseFormat = Some(ResponseFormat.JsonSchema("generated_object", schema, true)) }

        let result =
            generateInternal
                client
                model
                (Some prompt)
                Option.None
                Option.None
                Option.None
                0
                provider
                Option.None
                options

        // Anthropic emulates structured output via a forced tool call, so the JSON
        // lives in the tool-call arguments rather than in the text content.
        let text =
            let t = result.Text.Trim()
            if not (String.IsNullOrWhiteSpace t) then t
            else
                result.ToolCalls
                |> List.tryHead
                |> Option.map (fun tc -> tc.Arguments.Trim())
                |> Option.defaultValue ""

        if String.IsNullOrWhiteSpace(text) then
            raise (NoObjectGeneratedError("Provider returned an empty object response"))

        try
            validateJsonAgainstSchema schema text
        with
        | :? JsonException as ex ->
            raise (NoObjectGeneratedError(sprintf "Provider did not return valid JSON: %s" ex.Message))

        // Return a result with the extracted text so callers always see it in result.Text
        { result with
            Text = text
            Response = { result.Response with
                            Message = { result.Response.Message with
                                            Content = [ ContentPart.Text text ] } } }

    /// Backward-compatible structured output function.
    let generateObject
        (client: Client)
        (model: string)
        (prompt: string)
        (schema: string)
        (provider: string option)
        : GenerateResult =
        generateObjectWithControl client model prompt schema provider None

    /// High-level streaming function with multi-step tool loop support.
    let streamWithControl
        (client: Client)
        (model: string)
        (prompt: string option)
        (messages: Message list option)
        (system: string option)
        (tools: Tool list option)
        (maxToolRounds: int)
        (provider: string option)
        (reasoningEffort: string option)
        (maxRetries: int option)
        (timeout: TimeSpan option)
        (abortSignal: AbortSignal option)
        : StreamEvent seq =

        seq {
            let initialMessages = buildMessages prompt messages system
            let toolDefs = tools |> Option.map (List.map (fun t -> t.Definition))

            let options =
                { defaultRequestOptions with
                    MaxRetries = maxRetries
                    Timeout = timeout
                    AbortSignal = abortSignal }

            let mutable conversation = initialMessages
            let mutable stepIndex = 0
            let mutable keepLooping = true
            let mutable emittedStreamStart = false

            while keepLooping do
                let request = buildRequest model conversation toolDefs provider reasoningEffort options

                let events =
                    Retry.execute (maxRetriesConfig options.MaxRetries) (fun () ->
                        throwIfAborted abortSignal
                        client.Stream(request) |> Seq.toList)

                let mutable finishEvent: (FinishReason * Usage option * Response option) option = None

                for event in events do
                    match event with
                    | StreamStart when not emittedStreamStart ->
                        emittedStreamStart <- true
                        yield StreamStart
                    | StreamStart -> ()
                    | Finish(reason, usage, response) ->
                        finishEvent <- Some(reason, usage, response)
                    | _ ->
                        yield event

                match finishEvent with
                | None ->
                    keepLooping <- false
                    yield StreamError("Provider stream ended without a Finish event")
                | Some(reason, _usage, responseOpt) ->
                    let hasActiveTools =
                        match tools with
                        | Some ts -> ts |> List.exists (fun t -> t.Execute.IsSome)
                        | None -> false

                    match responseOpt with
                    | Some response when isToolCalls reason && hasActiveTools && stepIndex < maxToolRounds ->
                        let toolResults =
                            match tools with
                            | Some ts -> executeAllTools ts response.ToolCalls
                            | None -> []

                        conversation <- conversation @ [ response.Message ]
                        for result in toolResults do
                            conversation <- conversation @ [ Message.toolResult(result.ToolCallId, result.Content, result.IsError) ]

                        yield StepFinish(stepIndex, Some response)
                        stepIndex <- stepIndex + 1
                    | _ ->
                        keepLooping <- false
                        yield Finish(reason, responseOpt |> Option.map (fun r -> r.Usage), responseOpt)
        }

    /// Backward-compatible streaming function.
    let stream
        (client: Client)
        (model: string)
        (prompt: string option)
        (messages: Message list option)
        (system: string option)
        (provider: string option)
        : StreamEvent seq =
        streamWithControl
            client
            model
            prompt
            messages
            system
            None
            0
            provider
            None
            None
            None
            None

    let private tryDeserialize<'T> (text: string) =
        try
            Some(JsonSerializer.Deserialize<'T>(text))
        with _ ->
            None

    /// Streaming structured output.
    /// Emits partial objects whenever the accumulated JSON parses, and exposes the final parsed object.
    let streamObject<'T>
        (client: Client)
        (model: string)
        (prompt: string)
        (schema: string)
        (provider: string option)
        : StreamObjectResult<'T> =

        let request =
            { Request.Create(model, [ Message.user(prompt) ]) with
                Provider = provider
                ResponseFormat = Some(ResponseFormat.JsonSchema("generated_object", schema, true)) }

        let events = client.Stream(request) |> toAsyncEnumerable
        let accumulator = StreamAccumulator(events, model = model, provider = (provider |> Option.defaultValue ""))
        let mutable finalValue: 'T option = None
        let mutable finalComputed = false

        let partials =
            { new IAsyncEnumerable<'T> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    let inner = accumulator.TextStream.GetAsyncEnumerator(cancellationToken)
                    let text = System.Text.StringBuilder()
                    let mutable current = Unchecked.defaultof<'T>

                    { new IAsyncEnumerator<'T> with
                        member _.Current = current
                        member _.MoveNextAsync() =
                            let rec nextParsed () =
                                task {
                                    let! hasNext = inner.MoveNextAsync().AsTask()
                                    if not hasNext then
                                        if not finalComputed then
                                            finalComputed <- true
                                            finalValue <- tryDeserialize<'T> (accumulator.PartialResponse().Text.Trim())
                                        return false
                                    else
                                        text.Append(inner.Current) |> ignore
                                        match tryDeserialize<'T> (text.ToString().Trim()) with
                                        | Some parsed ->
                                            current <- parsed
                                            finalValue <- Some parsed
                                            return true
                                        | None ->
                                            return! nextParsed ()
                                }
                            ValueTask<bool>(nextParsed ())
                        member _.DisposeAsync() = inner.DisposeAsync() } }

        { PartialObjects = partials
          FinalObject = fun () ->
              if not finalComputed then
                  finalComputed <- true
                  finalValue <- tryDeserialize<'T> (accumulator.PartialResponse().Text.Trim())
              finalValue }
