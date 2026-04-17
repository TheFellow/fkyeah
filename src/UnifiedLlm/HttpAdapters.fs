namespace UnifiedLlm

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading

/// Global cancellation support — call Cancel() to abort all in-flight HTTP calls
module HttpCancellation =
    let mutable private cts = new CancellationTokenSource()

    let token () = cts.Token

    let cancel () =
        cts.Cancel()

    let isCancelled () = cts.IsCancellationRequested

    let reset () =
        cts.Dispose()
        cts <- new CancellationTokenSource()

module private HttpAdapterHelpers =

    type ToolBlockState = {
        Id: string
        Name: string
        Args: StringBuilder
        Metadata: Map<string, string>
    }

    let tryGetProperty (name: string) (element: JsonElement) : JsonElement option =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value
        else None

    let tryGetString (name: string) (element: JsonElement) : string option =
        tryGetProperty name element
        |> Option.bind (fun p ->
            if p.ValueKind = JsonValueKind.String then Some(p.GetString()) else None)

    let toObjMap (map: Map<string, string>) : obj =
        let d = Dictionary<string, obj>()
        for KeyValue(k, v) in map do
            d[k] <- v :> obj
        d :> obj

    let toRawElement (raw: string) : JsonElement =
        try
            JsonDocument.Parse(raw).RootElement.Clone()
        with _ ->
            JsonSerializer.SerializeToElement(dict [ "raw_text", raw ]).Clone()

    let tryParseJsonObject (json: string) (fallback: obj) : obj =
        try JsonDocument.Parse(json).RootElement.Clone() :> obj
        with _ -> fallback

    let private tryConvertJsonElementMap (element: JsonElement) : Map<string, obj> option =
        if element.ValueKind <> JsonValueKind.Object then
            None
        else
            element.EnumerateObject()
            |> Seq.map (fun p ->
                let boxed: obj =
                    match p.Value.ValueKind with
                    | JsonValueKind.String -> p.Value.GetString() :> obj
                    | JsonValueKind.True
                    | JsonValueKind.False -> p.Value.GetBoolean() :> obj
                    | JsonValueKind.Number ->
                        let mutable i = 0L
                        if p.Value.TryGetInt64(&i) then i :> obj else p.Value.GetDouble() :> obj
                    | _ -> p.Value.Clone() :> obj
                p.Name, boxed)
            |> Map.ofSeq
            |> Some

    let rec tryAsObjMap (value: obj) : Map<string, obj> option =
        match value with
        | null -> None
        | :? Map<string, obj> as m -> Some m
        | :? Map<string, string> as m ->
            m |> Map.map (fun _ v -> v :> obj) |> Some
        | :? IDictionary<string, obj> as dictObj ->
            dictObj |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq |> Some
        | :? JsonElement as je ->
            tryConvertJsonElementMap je
        | _ -> None

    let rec tryAsBool (value: obj) : bool option =
        match value with
        | null -> None
        | :? bool as b -> Some b
        | :? string as s ->
            let t = s.Trim().ToLowerInvariant()
            match t with
            | "true" | "1" | "yes" -> Some true
            | "false" | "0" | "no" -> Some false
            | _ -> None
        | :? JsonElement as je ->
            match je.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | JsonValueKind.String ->
                tryAsBool (je.GetString() :> obj)
            | _ -> None
        | _ -> None

    let rec toProviderOptionPayload (value: obj) : obj =
        match value with
        | :? Map<string, obj> as m ->
            let d = Dictionary<string, obj>()
            for KeyValue(k, v) in m do
                d[k] <- toProviderOptionPayload v
            d :> obj
        | :? JsonElement as je -> je.Clone() :> obj
        | _ -> value

    let mapAnthropicFinishReason (raw: string) =
        match raw with
        | "end_turn" -> Stop raw
        | "tool_use" -> ToolCalls raw
        | "max_tokens" -> Length raw
        | "stop_sequence" -> Stop raw
        | "refusal" -> ContentFilter raw
        | "error" -> Error raw
        | _ -> Other raw

    let mapOpenAIFinishReason (rawStatus: string) (rawReason: string option) (hasToolCalls: bool) =
        if hasToolCalls then ToolCalls (rawReason |> Option.defaultValue "tool_calls")
        else
            match rawStatus, rawReason with
            | "completed", _ -> Stop "completed"
            | "failed", Some r -> Error r
            | "failed", None -> Error "failed"
            | "incomplete", Some "max_output_tokens" -> Length "max_output_tokens"
            | "incomplete", Some "content_filter" -> ContentFilter "content_filter"
            | "incomplete", Some r -> Other r
            | "incomplete", None -> Length "incomplete"
            | s, Some r -> Other(sprintf "%s:%s" s r)
            | s, None -> Other s

    let mapGeminiFinishReason (raw: string) (hasToolCalls: bool) =
        if hasToolCalls then ToolCalls (if String.IsNullOrWhiteSpace(raw) then "function_call" else raw)
        else
            match raw with
            | "STOP" -> Stop raw
            | "MAX_TOKENS" -> Length raw
            | "SAFETY" | "RECITATION" -> ContentFilter raw
            | "" -> Stop "STOP"
            | "ERROR" -> Error raw
            | _ -> Other raw

    let tryGetHeader (httpResp: HttpResponseMessage) (name: string) : string option =
        match httpResp.Headers.TryGetValues(name) with
        | true, values -> values |> Seq.tryHead
        | _ ->
            match httpResp.Content.Headers.TryGetValues(name) with
            | true, values -> values |> Seq.tryHead
            | _ -> None

    let tryParseInt (value: string option) : int option =
        value
        |> Option.bind (fun v ->
            let mutable out = 0
            if Int32.TryParse(v, &out) then Some out else None)

    let tryParseResetAt (value: string option) : DateTimeOffset option =
        value
        |> Option.bind (fun v ->
            let mutable asInt = 0L
            if Int64.TryParse(v, &asInt) then
                Some(DateTimeOffset.FromUnixTimeSeconds(asInt))
            else
                let mutable dto = DateTimeOffset.MinValue
                if DateTimeOffset.TryParse(v, &dto) then Some dto else None)

    let parseRateLimit
        (httpResp: HttpResponseMessage)
        (limitHeaders: string list)
        (remainingHeaders: string list)
        (resetHeaders: string list)
        : RateLimitInfo option =
        let limit =
            limitHeaders
            |> List.tryPick (fun h -> tryGetHeader httpResp h)
            |> tryParseInt

        let remaining =
            remainingHeaders
            |> List.tryPick (fun h -> tryGetHeader httpResp h)
            |> tryParseInt

        let resetAt =
            resetHeaders
            |> List.tryPick (fun h -> tryGetHeader httpResp h)
            |> tryParseResetAt

        if limit.IsSome || remaining.IsSome || resetAt.IsSome then
            Some { Limit = limit; Remaining = remaining; ResetAt = resetAt }
        else None

    let parseAnthropicRateLimit (httpResp: HttpResponseMessage) =
        parseRateLimit
            httpResp
            [ "anthropic-ratelimit-requests-limit"; "anthropic-ratelimit-tokens-limit" ]
            [ "anthropic-ratelimit-requests-remaining"; "anthropic-ratelimit-tokens-remaining" ]
            [ "anthropic-ratelimit-requests-reset"; "anthropic-ratelimit-tokens-reset" ]

    let parseOpenAIRateLimit (httpResp: HttpResponseMessage) =
        parseRateLimit
            httpResp
            [ "x-ratelimit-limit-requests"; "x-ratelimit-limit-tokens" ]
            [ "x-ratelimit-remaining-requests"; "x-ratelimit-remaining-tokens" ]
            [ "x-ratelimit-reset-requests"; "x-ratelimit-reset-tokens" ]

    let parseGeminiRateLimit (httpResp: HttpResponseMessage) =
        parseRateLimit
            httpResp
            [ "x-ratelimit-limit-requests"; "x-ratelimit-limit" ]
            [ "x-ratelimit-remaining-requests"; "x-ratelimit-remaining" ]
            [ "x-ratelimit-reset-requests"; "x-ratelimit-reset" ]

    let createLinkedCancellationSource (request: Request) =
        let tokens = ResizeArray<CancellationToken>()
        tokens.Add(HttpCancellation.token())
        match request.AbortSignal with
        | Some signal -> tokens.Add(signal.Token)
        | None -> ()

        let cts =
            if tokens.Count > 0 then
                CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray())
            else
                new CancellationTokenSource()

        let timeoutCandidatesMs =
            [
                request.Timeout |> Option.map (fun timeout -> int timeout.TotalMilliseconds)
                request.TimeoutConfig |> Option.bind (fun timeout -> timeout.PerStepMs)
                request.AdapterTimeout |> Option.bind (fun timeout -> timeout.RequestMs)
            ]
            |> List.choose id
            |> List.filter (fun ms -> ms > 0)

        match timeoutCandidatesMs with
        | [] -> ()
        | values ->
            let minMs = values |> List.min
            cts.CancelAfter(TimeSpan.FromMilliseconds(float minMs))

        cts

    let raiseCancellation (request: Request) =
        match request.AbortSignal with
        | Some signal when signal.IsAborted -> raise (AbortError("Request aborted by caller"))
        | _ when HttpCancellation.isCancelled() -> raise (AbortError("Request cancelled"))
        | _ when request.Timeout.IsSome || request.TimeoutConfig.IsSome || request.AdapterTimeout.IsSome ->
            raise (RequestTimeoutError("Request timed out"))
        | _ -> raise (TimeoutError("Request timed out"))

    let sendAndReadString (client: HttpClient) (request: Request) (httpReq: HttpRequestMessage) =
        use cts = createLinkedCancellationSource request
        try
            let httpResp = client.Send(httpReq, cts.Token)
            let respBody = httpResp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult()
            httpResp, respBody
        with
        | :? OperationCanceledException ->
            raiseCancellation request

    let sendForStream (client: HttpClient) (request: Request) (httpReq: HttpRequestMessage) =
        let cts = createLinkedCancellationSource request
        try
            let httpResp = client.Send(httpReq, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            httpResp, cts
        with
        | :? OperationCanceledException ->
            cts.Dispose()
            raiseCancellation request

    let parseSse (reader: StreamReader) =
        seq {
            let mutable eventName = ""
            let dataLines = ResizeArray<string>()

            while not reader.EndOfStream do
                let line = reader.ReadLine()
                if isNull line then
                    ()
                elif line.StartsWith("event:") then
                    eventName <- line.Substring("event:".Length).Trim()
                elif line.StartsWith("data:") then
                    dataLines.Add(line.Substring("data:".Length).TrimStart())
                elif line = "" then
                    if dataLines.Count > 0 then
                        let name = if eventName = "" then "message" else eventName
                        let data = String.concat "\n" dataLines
                        yield name, data
                        eventName <- ""
                        dataLines.Clear()

            if dataLines.Count > 0 then
                let name = if eventName = "" then "message" else eventName
                let data = String.concat "\n" dataLines
                yield name, data
        }

    let extractMessagePartsFromAnthropic (content: JsonElement) =
        let contentParts = ResizeArray<ContentPart>()
        let warnings = ResizeArray<string>()

        for item in content.EnumerateArray() do
            match tryGetString "type" item with
            | Some "text" ->
                let text = tryGetString "text" item |> Option.defaultValue ""
                contentParts.Add(ContentPart.Text text)
            | Some "thinking" ->
                let text = tryGetString "thinking" item |> Option.defaultValue ""
                let signature = tryGetString "signature" item
                let redacted = text = "[redacted]"
                contentParts.Add(ContentPart.Thinking { Text = text; Signature = signature; Redacted = redacted })
            | Some "redacted_thinking" ->
                contentParts.Add(ContentPart.Thinking { Text = "[redacted]"; Signature = None; Redacted = true })
            | Some "tool_use" ->
                let args =
                    match tryGetProperty "input" item with
                    | Some input -> input.GetRawText()
                    | None -> "{}"
                contentParts.Add(ContentPart.ToolCall {
                    Id = tryGetString "id" item |> Option.defaultValue (Guid.NewGuid().ToString("N"))
                    Name = tryGetString "name" item |> Option.defaultValue "unknown_tool"
                    Arguments = args
                    Metadata = Map.empty
                })
            | Some other ->
                warnings.Add(sprintf "Unknown Anthropic content block type: %s" other)
            | None -> warnings.Add("Anthropic content block missing type")

        contentParts |> Seq.toList, warnings |> Seq.toList

    let responseFromAnthropic
        (requestModel: string)
        (respBody: string)
        (httpResp: HttpResponseMessage)
        =
        let doc = JsonDocument.Parse(respBody)
        let root = doc.RootElement

        let content =
            tryGetProperty "content" root
            |> Option.defaultWith (fun () -> JsonDocument.Parse("[]").RootElement)

        let contentParts, warnings = extractMessagePartsFromAnthropic content

        let usage: Usage =
            match tryGetProperty "usage" root with
            | Some u ->
                { InputTokens = tryGetProperty "input_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                  OutputTokens = tryGetProperty "output_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                  ReasoningTokens = None
                  CacheReadTokens = tryGetProperty "cache_read_input_tokens" u |> Option.map (fun v -> v.GetInt32())
                  CacheWriteTokens = tryGetProperty "cache_creation_input_tokens" u |> Option.map (fun v -> v.GetInt32()) }
            | None -> Usage.Zero

        let rawStopReason =
            tryGetString "stop_reason" root
            |> Option.defaultValue "end_turn"

        let finishReason = mapAnthropicFinishReason rawStopReason

        let message =
            { Role = Assistant
              Content = if contentParts.IsEmpty then [ ContentPart.Text "" ] else contentParts
              Name = None
              ToolCallId = None }

        { Id = tryGetString "id" root |> Option.defaultValue ("anthropic-" + Guid.NewGuid().ToString("N").Substring(0, 8))
          Model = tryGetString "model" root |> Option.defaultValue requestModel
          Provider = "anthropic"
          Message = message
          FinishReason = finishReason
          Usage = usage
          ResponseId = None
          Raw = Some(toRawElement respBody)
          Warnings = warnings
          RateLimit = parseAnthropicRateLimit httpResp }

    let responseFromOpenAI
        (requestModel: string)
        (respBody: string)
        (httpResp: HttpResponseMessage)
        =
        let doc = JsonDocument.Parse(respBody)
        let root = doc.RootElement

        let contentParts = ResizeArray<ContentPart>()
        let warnings = ResizeArray<string>()

        let mutable hasToolCalls = false

        let addMessageContent (item: JsonElement) =
            match tryGetProperty "content" item with
            | Some content ->
                for part in content.EnumerateArray() do
                    match tryGetString "type" part with
                    | Some "output_text" ->
                        contentParts.Add(ContentPart.Text(tryGetString "text" part |> Option.defaultValue ""))
                    | Some "reasoning" ->
                        let text = tryGetString "text" part |> Option.defaultValue ""
                        contentParts.Add(ContentPart.Thinking { Text = text; Signature = None; Redacted = false })
                    | Some other -> warnings.Add(sprintf "Unknown OpenAI message part type: %s" other)
                    | None -> ()
            | None -> ()

        match tryGetProperty "output" root with
        | Some output ->
            for item in output.EnumerateArray() do
                match tryGetString "type" item with
                | Some "message" -> addMessageContent item
                | Some "function_call" ->
                    hasToolCalls <- true
                    contentParts.Add(ContentPart.ToolCall {
                        Id = tryGetString "call_id" item |> Option.defaultValue (Guid.NewGuid().ToString("N"))
                        Name = tryGetString "name" item |> Option.defaultValue "unknown_tool"
                        Arguments = tryGetString "arguments" item |> Option.defaultValue "{}"
                        Metadata = Map.empty
                    })
                | Some "reasoning" ->
                    let text = tryGetString "summary" item |> Option.defaultValue ""
                    if text <> "" then
                        contentParts.Add(ContentPart.Thinking { Text = text; Signature = None; Redacted = false })
                | Some other -> warnings.Add(sprintf "Unknown OpenAI output item type: %s" other)
                | None -> ()
        | None ->
            match tryGetString "output_text" root with
            | Some text when text <> "" -> contentParts.Add(ContentPart.Text text)
            | _ -> ()

        let usage: Usage =
            match tryGetProperty "usage" root with
            | Some u ->
                let inputTokens = tryGetProperty "input_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                let outputTokens = tryGetProperty "output_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0

                let cacheReadTokens =
                    tryGetProperty "input_tokens_details" u
                    |> Option.bind (tryGetProperty "cached_tokens")
                    |> Option.map (fun v -> v.GetInt32())

                let reasoningTokens =
                    tryGetProperty "output_tokens_details" u
                    |> Option.bind (tryGetProperty "reasoning_tokens")
                    |> Option.map (fun v -> v.GetInt32())

                { InputTokens = inputTokens
                  OutputTokens = outputTokens
                  ReasoningTokens = reasoningTokens
                  CacheReadTokens = cacheReadTokens
                  CacheWriteTokens = None }
            | None -> Usage.Zero

        let status = tryGetString "status" root |> Option.defaultValue "completed"
        let incompleteReason =
            tryGetProperty "incomplete_details" root
            |> Option.bind (tryGetString "reason")

        let finishReason = mapOpenAIFinishReason status incompleteReason hasToolCalls

        { Id = tryGetString "id" root |> Option.defaultValue ("openai-" + Guid.NewGuid().ToString("N").Substring(0, 8))
          Model = tryGetString "model" root |> Option.defaultValue requestModel
          Provider = "openai"
          Message = { Role = Assistant; Content = (if contentParts.Count = 0 then [ ContentPart.Text "" ] else contentParts |> Seq.toList); Name = None; ToolCallId = None }
          FinishReason = finishReason
          Usage = usage
          ResponseId = tryGetString "id" root
          Raw = Some(toRawElement respBody)
          Warnings = warnings |> Seq.toList
          RateLimit = parseOpenAIRateLimit httpResp }

    let responseFromGemini
        (requestModel: string)
        (respBody: string)
        (httpResp: HttpResponseMessage)
        =
        let doc = JsonDocument.Parse(respBody)
        let root = doc.RootElement

        let contentParts = ResizeArray<ContentPart>()
        let warnings = ResizeArray<string>()

        let mutable hasToolCalls = false
        let mutable finishRaw = "STOP"

        match tryGetProperty "candidates" root with
        | Some candidates when candidates.GetArrayLength() > 0 ->
            let first = candidates.[0]
            finishRaw <- tryGetString "finishReason" first |> Option.defaultValue "STOP"

            match tryGetProperty "content" first |> Option.bind (tryGetProperty "parts") with
            | Some parts ->
                for part in parts.EnumerateArray() do
                    match tryGetProperty "text" part with
                    | Some textPart ->
                        contentParts.Add(ContentPart.Text(textPart.GetString()))
                    | None ->
                        match tryGetProperty "functionCall" part with
                        | Some fc ->
                            hasToolCalls <- true
                            let metadata =
                                match tryGetString "thoughtSignature" part with
                                | Some ts -> Map.ofList [ "thoughtSignature", ts ]
                                | None -> Map.empty
                            contentParts.Add(ContentPart.ToolCall {
                                Id = "gemini-tc-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                                Name = tryGetString "name" fc |> Option.defaultValue "unknown_tool"
                                Arguments =
                                    match tryGetProperty "args" fc with
                                    | Some args -> args.GetRawText()
                                    | None -> "{}"
                                Metadata = metadata
                            })
                        | None ->
                            warnings.Add("Unknown Gemini content part")
            | None -> ()
        | _ -> warnings.Add("Gemini response missing candidates")

        let usage: Usage =
            match tryGetProperty "usageMetadata" root with
            | Some u ->
                { InputTokens = tryGetProperty "promptTokenCount" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                  OutputTokens = tryGetProperty "candidatesTokenCount" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                  ReasoningTokens = tryGetProperty "thoughtsTokenCount" u |> Option.map (fun v -> v.GetInt32())
                  CacheReadTokens = tryGetProperty "cachedContentTokenCount" u |> Option.map (fun v -> v.GetInt32())
                  CacheWriteTokens = None }
            | None -> Usage.Zero

        let finishReason = mapGeminiFinishReason finishRaw hasToolCalls

        { Id = "gemini-" + Guid.NewGuid().ToString("N").Substring(0, 8)
          Model = requestModel
          Provider = "gemini"
          Message = { Role = Assistant; Content = (if contentParts.Count = 0 then [ ContentPart.Text "" ] else contentParts |> Seq.toList); Name = None; ToolCallId = None }
          FinishReason = finishReason
          Usage = usage
          ResponseId = None
          Raw = Some(toRawElement respBody)
          Warnings = warnings |> Seq.toList
          RateLimit = parseGeminiRateLimit httpResp }

/// Real Anthropic Messages API adapter
type AnthropicAdapter(apiKey: string) =
    let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
    let baseUrl =
        match Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") with
        | null
        | "" -> "https://api.anthropic.com/v1/messages"
        | value -> value

    let tryGetAnthropicOptions (request: Request) =
        request.ProviderOptions
        |> Option.bind (fun opts -> opts |> Map.tryFind "anthropic")
        |> Option.bind HttpAdapterHelpers.tryAsObjMap

    let isAutoCacheEnabled (request: Request) =
        tryGetAnthropicOptions request
        |> Option.bind (fun opts -> opts |> Map.tryFind "auto_cache")
        |> Option.bind HttpAdapterHelpers.tryAsBool
        |> Option.defaultValue true

    let buildBody (request: Request) (stream: bool) =
        let model =
            if request.Model = "" then "claude-sonnet-4-6"
            else request.Model

        let maxTokens = request.MaxTokens |> Option.defaultValue 4096

        let autoCache = isAutoCacheEnabled request

        let systemText =
            request.Messages
            |> List.choose (fun m -> if m.Role = System || m.Role = Developer then Some m.Text else None)
            |> String.concat "\n"

        let nonSystemMessages = request.Messages |> List.filter (fun m -> m.Role <> System && m.Role <> Developer)

        let lastUserIndex =
            nonSystemMessages
            |> List.mapi (fun i m -> i, m)
            |> List.filter (fun (_, m) -> m.Role = User || m.Role = Tool)
            |> List.tryLast
            |> Option.map fst

        let messages =
            nonSystemMessages
            |> List.mapi (fun i m ->
                let isLastUser = lastUserIndex = Some i
                match m.Role with
                | Assistant ->
                    let contentBlocks =
                        m.Content
                        |> List.map (fun part ->
                            match part with
                            | ContentPart.Text t -> {| ``type`` = "text"; text = t |} :> obj
                            | ContentPart.ToolCall tc ->
                                let inputObj = HttpAdapterHelpers.tryParseJsonObject tc.Arguments ({||} :> obj)
                                {| ``type`` = "tool_use"; id = tc.Id; name = tc.Name; input = inputObj |} :> obj
                            | ContentPart.Thinking td ->
                                {| ``type`` = "thinking"; thinking = td.Text; signature = td.Signature |} :> obj
                            | _ -> {| ``type`` = "text"; text = "" |} :> obj)
                        |> Array.ofList
                    {| role = "assistant"; content = contentBlocks |} :> obj
                | Tool ->
                    let resultContent =
                        m.Content |> List.tryPick (fun p -> match p with | ContentPart.ToolResult tr -> Some tr | _ -> None)
                    match resultContent with
                    | Some tr ->
                        let block =
                            if autoCache && isLastUser then
                                {| ``type`` = "tool_result"
                                   tool_use_id = tr.ToolCallId
                                   content = tr.Content
                                   is_error = tr.IsError
                                   cache_control = {| ``type`` = "ephemeral" |} |} :> obj
                            else
                                {| ``type`` = "tool_result"
                                   tool_use_id = tr.ToolCallId
                                   content = tr.Content
                                   is_error = tr.IsError |} :> obj
                        {| role = "user"; content = [| block |] |} :> obj
                    | None ->
                        {| role = "user"; content = [| {| ``type`` = "text"; text = m.Text |} |] |} :> obj
                | _ ->
                    let block =
                        if autoCache && isLastUser then
                            {| ``type`` = "text"; text = m.Text; cache_control = {| ``type`` = "ephemeral" |} |} :> obj
                        else
                            {| ``type`` = "text"; text = m.Text |} :> obj
                    {| role = "user"; content = [| block |] |} :> obj)

        let bodyDict = Dictionary<string, obj>()
        bodyDict["model"] <- model
        bodyDict["max_tokens"] <- maxTokens
        bodyDict["messages"] <- messages

        if systemText <> "" then
            if autoCache then
                bodyDict["system"] <- [| {| ``type`` = "text"; text = systemText; cache_control = {| ``type`` = "ephemeral" |} |} |]
            else
                bodyDict["system"] <- [| {| ``type`` = "text"; text = systemText |} |]

        if stream then
            bodyDict["stream"] <- true

        match request.Temperature with
        | Some t -> bodyDict["temperature"] <- t
        | None -> ()

        match request.TopP with
        | Some p -> bodyDict["top_p"] <- p
        | None -> ()

        match request.StopSequences with
        | Some stops when not stops.IsEmpty -> bodyDict["stop_sequences"] <- stops |> List.toArray
        | _ -> ()

        match request.Metadata with
        | Some m when not m.IsEmpty -> bodyDict["metadata"] <- HttpAdapterHelpers.toObjMap m
        | _ -> ()

        let thinkingBudget =
            match request.ReasoningEffort with
            | Some "low" -> Some 2048
            | Some "medium" -> Some 8192
            | Some "high" -> Some 32768
            | Some "xhigh" -> Some 65536
            | _ -> None

        let isAdaptiveThinkingModel (m: string) =
            m.Contains("opus-4-7") || m.Contains("sonnet-4-7") || m.Contains("haiku-4-7")

        let adaptiveEffort =
            match request.ReasoningEffort with
            | Some "low" -> Some "low"
            | Some "medium" -> Some "medium"
            | Some "high" -> Some "high"
            | Some "xhigh" -> Some "high"
            | _ -> None

        match thinkingBudget, adaptiveEffort with
        | Some _, Some effort when isAdaptiveThinkingModel model ->
            bodyDict["thinking"] <- {| ``type`` = "adaptive" |}
            bodyDict["output_config"] <- {| effort = effort |}
        | Some budget, _ ->
            bodyDict["thinking"] <- {| ``type`` = "enabled"; budget_tokens = budget |}
            // max_tokens must be greater than thinking.budget_tokens per Anthropic API
            if maxTokens <= budget then
                bodyDict["max_tokens"] <- budget + 4096
        | None, _ -> ()

        let structuredToolName, structuredToolDef =
            match request.ResponseFormat with
            | Some(ResponseFormat.JsonSchema(name, schema, _strict)) ->
                let schemaObj = HttpAdapterHelpers.tryParseJsonObject schema ({| ``type`` = "object" |} :> obj)
                let toolName = if String.IsNullOrWhiteSpace(name) then "extract_structured_output" else name
                let tool =
                    {| name = toolName
                       description = "Return the final response as structured JSON."
                       input_schema = schemaObj |} :> obj
                Some toolName, Some tool
            | Some ResponseFormat.JsonObject ->
                let toolName = "extract_structured_output"
                let tool =
                    {| name = toolName
                       description = "Return the final response as structured JSON object."
                       input_schema = ({| ``type`` = "object" |} :> obj) |} :> obj
                Some toolName, Some tool
            | _ -> None, None

        let toolDefs = ResizeArray<obj>()
        match request.Tools with
        | Some tools when not tools.IsEmpty ->
            for t in tools do
                let inputSchema = HttpAdapterHelpers.tryParseJsonObject t.Parameters ({| ``type`` = "object" |} :> obj)
                toolDefs.Add({| name = t.Name; description = t.Description; input_schema = inputSchema |} :> obj)
        | _ -> ()

        match structuredToolDef with
        | Some t -> toolDefs.Add(t)
        | None -> ()

        let disableToolsByChoice =
            match request.ToolChoice, structuredToolDef with
            | Some ToolChoice.None, None -> true
            | _ -> false

        if not disableToolsByChoice && toolDefs.Count > 0 then
            bodyDict["tools"] <- toolDefs.ToArray()

            match structuredToolName with
            | Some name -> bodyDict["tool_choice"] <- {| ``type`` = "tool"; name = name |}
            | None ->
                match request.ToolChoice with
                | Some ToolChoice.Auto -> bodyDict["tool_choice"] <- {| ``type`` = "auto" |}
                | Some ToolChoice.Required -> bodyDict["tool_choice"] <- {| ``type`` = "any" |}
                | Some(ToolChoice.Named name) -> bodyDict["tool_choice"] <- {| ``type`` = "tool"; name = name |}
                | _ -> ()

        bodyDict

    member private _.BuildHttpRequest(request: Request, stream: bool) =
        let body = JsonSerializer.Serialize(buildBody request stream)
        let httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        httpReq.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        httpReq.Headers.Add("x-api-key", apiKey)
        httpReq.Headers.Add("anthropic-version", "2023-06-01")

        let betaFeatures = ResizeArray<string>()
        if request.ReasoningEffort.IsSome then
            betaFeatures.Add("interleaved-thinking-2025-05-14")

        let addFeature (feature: string) =
            let trimmed = feature.Trim()
            if trimmed <> "" && not (betaFeatures.Contains(trimmed)) then
                betaFeatures.Add(trimmed)

        match tryGetAnthropicOptions request |> Option.bind (fun opts -> opts |> Map.tryFind "beta_features") with
        | Some (:? string as features) ->
            for feature in features.Split(',') do
                addFeature feature
        | Some (:? JsonElement as je) when je.ValueKind = JsonValueKind.Array ->
            for item in je.EnumerateArray() do
                if item.ValueKind = JsonValueKind.String then
                    addFeature (item.GetString())
        | _ -> ()

        if betaFeatures.Count > 0 then
            httpReq.Headers.Add("anthropic-beta", String.concat "," betaFeatures)

        httpReq

    interface IProviderAdapter with
        member _.ProviderId = "anthropic"
        member _.Initialize() = async.Return()
        member _.Close() =
            async {
                client.Dispose()
            }
        member _.SupportsToolChoice() = true

        member this.Complete(request: Request) =
            let model = if request.Model = "" then "claude-sonnet-4-6" else request.Model
            let httpReq = this.BuildHttpRequest(request, false)
            let httpResp, respBody = HttpAdapterHelpers.sendAndReadString client request httpReq

            if not httpResp.IsSuccessStatusCode then
                let err = ErrorMapping.classifyHttpResponse httpResp respBody
                raise err

            HttpAdapterHelpers.responseFromAnthropic model respBody httpResp

        member this.Stream(request: Request) =
            let model = if request.Model = "" then "claude-sonnet-4-6" else request.Model
            seq {
                let httpReq = this.BuildHttpRequest(request, true)
                let httpResp, cts = HttpAdapterHelpers.sendForStream client request httpReq
                use _cts = cts

                if not httpResp.IsSuccessStatusCode then
                    let body = httpResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    let err = ErrorMapping.classifyHttpResponse httpResp body
                    raise err

                use stream = httpResp.Content.ReadAsStream(cts.Token)
                use reader = new StreamReader(stream)

                let textBuilders = Dictionary<int, StringBuilder>()
                let textStarted = HashSet<int>()
                let toolStates = Dictionary<int, HttpAdapterHelpers.ToolBlockState>()
                let reasoningBuilders = Dictionary<int, StringBuilder>()
                let contentParts = ResizeArray<ContentPart>()
                let rawEvents = ResizeArray<string>()

                let mutable responseId = "anthropic-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                let mutable responseModel = model
                let mutable usage = Usage.Zero
                let mutable finishReason = Stop "end_turn"
                let mutable warnings: string list = []

                yield StreamStart

                for (eventName, data) in HttpAdapterHelpers.parseSse reader do
                    if data <> "[DONE]" then
                        rawEvents.Add(sprintf "%s:%s" eventName data)
                        match eventName with
                        | "message_start" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            match HttpAdapterHelpers.tryGetProperty "message" root with
                            | Some msg ->
                                responseId <- HttpAdapterHelpers.tryGetString "id" msg |> Option.defaultValue responseId
                                responseModel <- HttpAdapterHelpers.tryGetString "model" msg |> Option.defaultValue responseModel
                                match HttpAdapterHelpers.tryGetProperty "usage" msg with
                                | Some u ->
                                    usage <- { usage with InputTokens = HttpAdapterHelpers.tryGetProperty "input_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue usage.InputTokens }
                                | None -> ()
                            | None -> ()
                        | "content_block_start" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let idx = HttpAdapterHelpers.tryGetProperty "index" root |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                            match HttpAdapterHelpers.tryGetProperty "content_block" root with
                            | Some block ->
                                match HttpAdapterHelpers.tryGetString "type" block with
                                | Some "text" ->
                                    textBuilders[idx] <- StringBuilder()
                                    if textStarted.Add(idx) then
                                        yield TextStart(sprintf "text-%d" idx)
                                | Some "tool_use" ->
                                    let state: HttpAdapterHelpers.ToolBlockState = {
                                        Id = HttpAdapterHelpers.tryGetString "id" block |> Option.defaultValue ("tool-" + Guid.NewGuid().ToString("N"))
                                        Name = HttpAdapterHelpers.tryGetString "name" block |> Option.defaultValue "unknown_tool"
                                        Args = StringBuilder()
                                        Metadata = Map.empty
                                    }
                                    match HttpAdapterHelpers.tryGetProperty "input" block with
                                    | Some input -> state.Args.Append(input.GetRawText()) |> ignore
                                    | None -> ()
                                    toolStates[idx] <- state
                                    yield ToolCallStart { Id = state.Id; Name = state.Name; Arguments = state.Args.ToString(); Metadata = state.Metadata }
                                | Some "thinking" ->
                                    reasoningBuilders[idx] <- StringBuilder()
                                    yield ReasoningStart None
                                | _ -> ()
                            | None -> ()
                        | "content_block_delta" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let idx = HttpAdapterHelpers.tryGetProperty "index" root |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                            match HttpAdapterHelpers.tryGetProperty "delta" root with
                            | Some delta ->
                                match HttpAdapterHelpers.tryGetString "type" delta with
                                | Some "text_delta" ->
                                    let chunk = HttpAdapterHelpers.tryGetString "text" delta |> Option.defaultValue ""
                                    if chunk <> "" then
                                        if not (textBuilders.ContainsKey(idx)) then textBuilders[idx] <- StringBuilder()
                                        textBuilders[idx].Append(chunk) |> ignore
                                        yield TextDelta(Some(sprintf "text-%d" idx), chunk)
                                | Some "input_json_delta" ->
                                    let chunk = HttpAdapterHelpers.tryGetString "partial_json" delta |> Option.defaultValue ""
                                    if chunk <> "" && toolStates.ContainsKey(idx) then
                                        toolStates[idx].Args.Append(chunk) |> ignore
                                        yield ToolCallDelta(toolStates[idx].Id, chunk)
                                | Some "thinking_delta" ->
                                    let chunk = HttpAdapterHelpers.tryGetString "thinking" delta |> Option.defaultValue ""
                                    if chunk <> "" then
                                        if not (reasoningBuilders.ContainsKey(idx)) then reasoningBuilders[idx] <- StringBuilder()
                                        reasoningBuilders[idx].Append(chunk) |> ignore
                                        yield ThinkingEvent chunk
                                | _ -> ()
                            | None -> ()
                        | "content_block_stop" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let idx = HttpAdapterHelpers.tryGetProperty "index" root |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0

                            if textBuilders.ContainsKey(idx) then
                                let text = textBuilders[idx].ToString()
                                if text <> "" then contentParts.Add(ContentPart.Text text)
                                yield TextEnd(sprintf "text-%d" idx)
                                textBuilders.Remove(idx) |> ignore

                            if toolStates.ContainsKey(idx) then
                                let tc: ToolCallData =
                                    { Id = toolStates[idx].Id
                                      Name = toolStates[idx].Name
                                      Arguments = toolStates[idx].Args.ToString()
                                      Metadata = toolStates[idx].Metadata }
                                contentParts.Add(ContentPart.ToolCall tc)
                                yield ToolCallEnd tc
                                toolStates.Remove(idx) |> ignore

                            if reasoningBuilders.ContainsKey(idx) then
                                let t = reasoningBuilders[idx].ToString()
                                if t <> "" then contentParts.Add(ContentPart.Thinking { Text = t; Signature = None; Redacted = false })
                                yield ReasoningEnd None
                                reasoningBuilders.Remove(idx) |> ignore
                        | "message_delta" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement

                            match HttpAdapterHelpers.tryGetProperty "delta" root |> Option.bind (HttpAdapterHelpers.tryGetString "stop_reason") with
                            | Some raw -> finishReason <- HttpAdapterHelpers.mapAnthropicFinishReason raw
                            | None -> ()

                            match HttpAdapterHelpers.tryGetProperty "usage" root with
                            | Some u ->
                                usage <-
                                    { usage with
                                        OutputTokens = HttpAdapterHelpers.tryGetProperty "output_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue usage.OutputTokens
                                        CacheReadTokens =
                                            HttpAdapterHelpers.tryGetProperty "cache_read_input_tokens" u
                                            |> Option.map (fun v -> v.GetInt32())
                                        CacheWriteTokens =
                                            HttpAdapterHelpers.tryGetProperty "cache_creation_input_tokens" u
                                            |> Option.map (fun v -> v.GetInt32()) }
                            | None -> ()
                        | "message_stop" ->
                            let message =
                                { Role = Assistant
                                  Content = if contentParts.Count = 0 then [ ContentPart.Text "" ] else contentParts |> Seq.toList
                                  Name = None
                                  ToolCallId = None }

                            let response =
                                { Id = responseId
                                  Model = responseModel
                                  Provider = "anthropic"
                                  Message = message
                                  FinishReason = finishReason
                                  Usage = usage
                                  ResponseId = None
                                  Raw = Some(HttpAdapterHelpers.toRawElement (String.concat "\n" rawEvents))
                                  Warnings = warnings
                                  RateLimit = HttpAdapterHelpers.parseAnthropicRateLimit httpResp }

                            yield Finish(finishReason, Some usage, Some response)
                        | "error" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let message =
                                HttpAdapterHelpers.tryGetProperty "error" root
                                |> Option.bind (HttpAdapterHelpers.tryGetString "message")
                                |> Option.defaultValue data
                            yield StreamError message
                        | _ ->
                            yield ProviderEvent(eventName, data)
            }

/// Real OpenAI Responses API adapter
type OpenAIAdapter(apiKey: string) =
    let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
    let baseUrl =
        match Environment.GetEnvironmentVariable("OPENAI_BASE_URL") with
        | null
        | "" -> "https://api.openai.com/v1/responses"
        | value -> value

    let buildBody (request: Request) (stream: bool) =
        let model = if request.Model = "" then "gpt-4o" else request.Model

        let instructions =
            request.Messages
            |> List.choose (fun m -> if m.Role = System || m.Role = Developer then Some m.Text else None)
            |> String.concat "\n"

        let input =
            request.Messages
            |> List.filter (fun m -> m.Role <> System && m.Role <> Developer)
            |> List.collect (fun m ->
                match m.Role with
                | Assistant ->
                    let hasToolCalls = m.Content |> List.exists (function ContentPart.ToolCall _ -> true | _ -> false)
                    if hasToolCalls then
                        m.Content
                        |> List.choose (fun p ->
                            match p with
                            | ContentPart.Text t when t <> "" ->
                                Some ({| ``type`` = "message"; role = "assistant"; content = [| {| ``type`` = "output_text"; text = t |} |] |} :> obj)
                            | ContentPart.ToolCall tc ->
                                Some ({| ``type`` = "function_call"; call_id = tc.Id; name = tc.Name; arguments = tc.Arguments |} :> obj)
                            | _ -> None)
                    else
                        [ {| ``type`` = "message"; role = "assistant"; content = [| {| ``type`` = "output_text"; text = m.Text |} |] |} :> obj ]
                | Tool ->
                    m.Content
                    |> List.choose (fun p ->
                        match p with
                        | ContentPart.ToolResult tr ->
                            Some ({| ``type`` = "function_call_output"; call_id = tr.ToolCallId; output = tr.Content |} :> obj)
                        | _ -> None)
                | _ ->
                    [ {| ``type`` = "message"; role = "user"; content = [| {| ``type`` = "input_text"; text = m.Text |} |] |} :> obj ])

        let bodyDict = Dictionary<string, obj>()
        bodyDict["model"] <- model
        bodyDict["input"] <- input
        bodyDict["store"] <- true

        if instructions <> "" then bodyDict["instructions"] <- instructions
        if stream then bodyDict["stream"] <- true

        match request.PreviousResponseId with
        | Some prevId -> bodyDict["previous_response_id"] <- prevId
        | None -> ()

        match request.MaxTokens with
        | Some mt -> bodyDict["max_output_tokens"] <- mt
        | None -> ()

        match request.ReasoningEffort with
        | Some re -> bodyDict["reasoning"] <- {| effort = re |}
        | None -> ()

        match request.Temperature with
        | Some t -> bodyDict["temperature"] <- t
        | None -> ()

        match request.TopP with
        | Some p -> bodyDict["top_p"] <- p
        | None -> ()

        match request.StopSequences with
        | Some stops when not stops.IsEmpty -> bodyDict["stop"] <- stops |> List.toArray
        | _ -> ()

        match request.Metadata with
        | Some m when not m.IsEmpty -> bodyDict["metadata"] <- HttpAdapterHelpers.toObjMap m
        | _ -> ()

        match request.ResponseFormat with
        | Some(ResponseFormat.JsonSchema(name, schema, strict)) ->
            let schemaObj = HttpAdapterHelpers.tryParseJsonObject schema ({| ``type`` = "object" |} :> obj)
            bodyDict["text"] <- {| format = {| ``type`` = "json_schema"; name = name; schema = schemaObj; strict = strict |} |}
        | Some ResponseFormat.JsonObject ->
            bodyDict["text"] <- {| format = {| ``type`` = "json_object" |} |}
        | _ -> ()

        match request.Tools with
        | Some tools when not tools.IsEmpty ->
            let toolDefs =
                tools
                |> List.map (fun t ->
                    let parameters = HttpAdapterHelpers.tryParseJsonObject t.Parameters ({| ``type`` = "object" |} :> obj)
                    {| ``type`` = "function"; name = t.Name; description = t.Description; parameters = parameters |} :> obj)
                |> List.toArray
            bodyDict["tools"] <- toolDefs

            match request.ToolChoice with
            | Some ToolChoice.Auto -> bodyDict["tool_choice"] <- "auto"
            | Some ToolChoice.None -> bodyDict["tool_choice"] <- "none"
            | Some ToolChoice.Required -> bodyDict["tool_choice"] <- "required"
            | Some(ToolChoice.Named name) -> bodyDict["tool_choice"] <- {| ``type`` = "function"; name = name |}
            | None -> ()
        | _ -> ()

        bodyDict

    member private _.BuildHttpRequest(request: Request, stream: bool) =
        let body = JsonSerializer.Serialize(buildBody request stream)
        let httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        httpReq.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        httpReq.Headers.Add("Authorization", $"Bearer {apiKey}")
        httpReq

    interface IProviderAdapter with
        member _.ProviderId = "openai"
        member _.Initialize() = async.Return()
        member _.Close() =
            async {
                client.Dispose()
            }
        member _.SupportsToolChoice() = true

        member this.Complete(request: Request) =
            let model = if request.Model = "" then "gpt-4o" else request.Model
            let httpReq = this.BuildHttpRequest(request, false)
            let httpResp, respBody = HttpAdapterHelpers.sendAndReadString client request httpReq

            if not httpResp.IsSuccessStatusCode then
                let err = ErrorMapping.classifyHttpResponse httpResp respBody
                raise err

            HttpAdapterHelpers.responseFromOpenAI model respBody httpResp

        member this.Stream(request: Request) =
            let model = if request.Model = "" then "gpt-4o" else request.Model
            seq {
                let httpReq = this.BuildHttpRequest(request, true)
                let httpResp, cts = HttpAdapterHelpers.sendForStream client request httpReq
                use _cts = cts

                if not httpResp.IsSuccessStatusCode then
                    let body = httpResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    let err = ErrorMapping.classifyHttpResponse httpResp body
                    raise err

                use stream = httpResp.Content.ReadAsStream(cts.Token)
                use reader = new StreamReader(stream)

                let textBuffer = StringBuilder()
                let toolStates = Dictionary<string, HttpAdapterHelpers.ToolBlockState>()
                let toolOrder = ResizeArray<string>()
                let rawEvents = ResizeArray<string>()
                let contentParts = ResizeArray<ContentPart>()

                let mutable responseId: string option = None
                let mutable responseModel = model
                let mutable usage = Usage.Zero
                let mutable finishReason = Stop "completed"
                let mutable textStarted = false

                yield StreamStart

                for (eventName, data) in HttpAdapterHelpers.parseSse reader do
                    if data <> "[DONE]" then
                        rawEvents.Add(sprintf "%s:%s" eventName data)
                        match eventName with
                        | "response.created" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            responseId <- HttpAdapterHelpers.tryGetString "id" root
                            responseModel <- HttpAdapterHelpers.tryGetString "model" root |> Option.defaultValue responseModel
                        | "response.output_item.added" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            match HttpAdapterHelpers.tryGetProperty "item" root with
                            | Some item ->
                                match HttpAdapterHelpers.tryGetString "type" item with
                                | Some "function_call" ->
                                    let id = HttpAdapterHelpers.tryGetString "call_id" item |> Option.defaultValue ("call-" + Guid.NewGuid().ToString("N"))
                                    let name = HttpAdapterHelpers.tryGetString "name" item |> Option.defaultValue "unknown_tool"
                                    let args = HttpAdapterHelpers.tryGetString "arguments" item |> Option.defaultValue ""
                                    let state: HttpAdapterHelpers.ToolBlockState = { Id = id; Name = name; Args = StringBuilder(args); Metadata = Map.empty }
                                    toolStates[id] <- state
                                    if not (toolOrder.Contains(id)) then toolOrder.Add(id)
                                    yield ToolCallStart { Id = id; Name = name; Arguments = args; Metadata = Map.empty }
                                | Some "reasoning" ->
                                    yield ReasoningStart None
                                | _ -> ()
                            | None -> ()
                        | "response.content_part.delta"
                        | "response.output_text.delta" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let delta =
                                HttpAdapterHelpers.tryGetString "delta" root
                                |> Option.orElseWith (fun () ->
                                    HttpAdapterHelpers.tryGetProperty "part" root
                                    |> Option.bind (HttpAdapterHelpers.tryGetString "text"))
                                |> Option.defaultValue ""
                            if delta <> "" then
                                if not textStarted then
                                    textStarted <- true
                                    yield TextStart "text-0"
                                textBuffer.Append(delta) |> ignore
                                yield TextDelta(Some "text-0", delta)
                        | "response.function_call_arguments.delta" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let id = HttpAdapterHelpers.tryGetString "call_id" root |> Option.defaultValue ""
                            let delta = HttpAdapterHelpers.tryGetString "delta" root |> Option.defaultValue ""
                            if id <> "" && delta <> "" && toolStates.ContainsKey(id) then
                                toolStates[id].Args.Append(delta) |> ignore
                                yield ToolCallDelta(id, delta)
                        | "response.output_item.done" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            match HttpAdapterHelpers.tryGetProperty "item" root with
                            | Some item ->
                                match HttpAdapterHelpers.tryGetString "type" item with
                                | Some "function_call" ->
                                    let id = HttpAdapterHelpers.tryGetString "call_id" item |> Option.defaultValue ""
                                    if id <> "" && toolStates.ContainsKey(id) then
                                        let state = toolStates[id]
                                        let tc: ToolCallData =
                                            { Id = state.Id
                                              Name = state.Name
                                              Arguments = state.Args.ToString()
                                              Metadata = state.Metadata }
                                        yield ToolCallEnd tc
                                | Some "reasoning" ->
                                    yield ReasoningEnd None
                                | _ -> ()
                            | None -> ()
                        | "response.completed" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement

                            let responseElement =
                                HttpAdapterHelpers.tryGetProperty "response" root
                                |> Option.defaultValue root

                            let responseText = textBuffer.ToString()
                            if responseText <> "" then contentParts.Add(ContentPart.Text responseText)

                            for id in toolOrder do
                                if toolStates.ContainsKey(id) then
                                    let state = toolStates[id]
                                    contentParts.Add(ContentPart.ToolCall { Id = state.Id; Name = state.Name; Arguments = state.Args.ToString(); Metadata = state.Metadata })

                            let status = HttpAdapterHelpers.tryGetString "status" responseElement |> Option.defaultValue "completed"
                            let incompleteReason =
                                HttpAdapterHelpers.tryGetProperty "incomplete_details" responseElement
                                |> Option.bind (HttpAdapterHelpers.tryGetString "reason")

                            let hasToolCalls = toolOrder.Count > 0
                            finishReason <- HttpAdapterHelpers.mapOpenAIFinishReason status incompleteReason hasToolCalls

                            match HttpAdapterHelpers.tryGetProperty "usage" responseElement with
                            | Some u ->
                                usage <-
                                    { InputTokens = HttpAdapterHelpers.tryGetProperty "input_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                                      OutputTokens = HttpAdapterHelpers.tryGetProperty "output_tokens" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue 0
                                      ReasoningTokens =
                                        HttpAdapterHelpers.tryGetProperty "output_tokens_details" u
                                        |> Option.bind (HttpAdapterHelpers.tryGetProperty "reasoning_tokens")
                                        |> Option.map (fun v -> v.GetInt32())
                                      CacheReadTokens =
                                        HttpAdapterHelpers.tryGetProperty "input_tokens_details" u
                                        |> Option.bind (HttpAdapterHelpers.tryGetProperty "cached_tokens")
                                        |> Option.map (fun v -> v.GetInt32())
                                      CacheWriteTokens = None }
                            | None -> ()

                            if textStarted then
                                yield TextEnd "text-0"

                            let response =
                                { Id = responseId |> Option.defaultValue ("openai-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                                  Model = responseModel
                                  Provider = "openai"
                                  Message = { Role = Assistant; Content = (if contentParts.Count = 0 then [ ContentPart.Text "" ] else contentParts |> Seq.toList); Name = None; ToolCallId = None }
                                  FinishReason = finishReason
                                  Usage = usage
                                  ResponseId = responseId
                                  Raw = Some(HttpAdapterHelpers.toRawElement (String.concat "\n" rawEvents))
                                  Warnings = []
                                  RateLimit = HttpAdapterHelpers.parseOpenAIRateLimit httpResp }

                            yield Finish(finishReason, Some usage, Some response)
                        | "response.failed" ->
                            let doc = JsonDocument.Parse(data)
                            let root = doc.RootElement
                            let message =
                                HttpAdapterHelpers.tryGetProperty "error" root
                                |> Option.bind (HttpAdapterHelpers.tryGetString "message")
                                |> Option.defaultValue "OpenAI stream failed"
                            yield StreamError message
                            yield Finish(Error "response.failed", None, None)
                        | _ -> yield ProviderEvent(eventName, data)
            }

/// Real Gemini API adapter (v1beta generateContent)
type GeminiAdapter(apiKey: string) =
    let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

    let tryGetGeminiOptions (request: Request) =
        request.ProviderOptions
        |> Option.bind (fun opts -> opts |> Map.tryFind "gemini")
        |> Option.bind HttpAdapterHelpers.tryAsObjMap

    let buildBody (request: Request) =
        let systemText =
            request.Messages
            |> List.choose (fun m -> if m.Role = System || m.Role = Developer then Some m.Text else None)
            |> String.concat "\n"

        let toolCallNameMap =
            request.Messages
            |> List.collect (fun m ->
                m.Content |> List.choose (fun p ->
                    match p with
                    | ContentPart.ToolCall tc -> Some(tc.Id, tc.Name)
                    | _ -> None))
            |> Map.ofList

        let contents =
            request.Messages
            |> List.filter (fun m -> m.Role <> System && m.Role <> Developer)
            |> List.map (fun m ->
                match m.Role with
                | Assistant ->
                    let parts =
                        m.Content
                        |> List.map (fun p ->
                            match p with
                            | ContentPart.Text t -> {| text = t |} :> obj
                            | ContentPart.ToolCall tc ->
                                let args = HttpAdapterHelpers.tryParseJsonObject tc.Arguments ({||} :> obj)
                                match tc.Metadata |> Map.tryFind "thoughtSignature" with
                                | Some ts -> {| functionCall = {| name = tc.Name; args = args |}; thoughtSignature = ts |} :> obj
                                | None -> {| functionCall = {| name = tc.Name; args = args |} |} :> obj
                            | ContentPart.Thinking td ->
                                match td.Signature with
                                | Some sigValue -> {| text = td.Text; thoughtSignature = sigValue |} :> obj
                                | None -> {| text = td.Text |} :> obj
                            | _ -> {| text = "" |} :> obj)
                        |> Array.ofList
                    {| role = "model"; parts = parts |} :> obj
                | Tool ->
                    let parts =
                        m.Content
                        |> List.choose (fun p ->
                            match p with
                            | ContentPart.ToolResult tr ->
                                let name = toolCallNameMap |> Map.tryFind tr.ToolCallId |> Option.defaultValue "unknown"
                                let responseObj = HttpAdapterHelpers.tryParseJsonObject tr.Content ({| result = tr.Content |} :> obj)
                                Some ({| functionResponse = {| name = name; response = responseObj |} |} :> obj)
                            | _ -> None)
                        |> Array.ofList
                    {| role = "user"; parts = parts |} :> obj
                | _ ->
                    {| role = "user"; parts = [| {| text = m.Text |} :> obj |] |} :> obj)

        let bodyObj = Dictionary<string, obj>()
        bodyObj["contents"] <- contents

        if systemText <> "" then
            bodyObj["systemInstruction"] <- {| parts = [| {| text = systemText |} |] |}

        match request.Metadata with
        | Some m when not m.IsEmpty -> bodyObj["labels"] <- HttpAdapterHelpers.toObjMap m
        | _ -> ()

        match request.Tools with
        | Some tools when not tools.IsEmpty ->
            let funcDecls =
                tools
                |> List.map (fun t ->
                    let parameters = HttpAdapterHelpers.tryParseJsonObject t.Parameters ({| ``type`` = "object" |} :> obj)
                    {| name = t.Name; description = t.Description; parameters = parameters |} :> obj)
                |> List.toArray
            bodyObj["tools"] <- [| {| function_declarations = funcDecls |} |]
        | _ -> ()

        let genConfig = Dictionary<string, obj>()

        match request.MaxTokens with
        | Some mt -> genConfig["maxOutputTokens"] <- mt
        | None -> ()

        match request.Temperature with
        | Some t -> genConfig["temperature"] <- t
        | None -> ()

        match request.TopP with
        | Some p -> genConfig["topP"] <- p
        | None -> ()

        match request.StopSequences with
        | Some stops when not stops.IsEmpty -> genConfig["stopSequences"] <- stops |> List.toArray
        | _ -> ()

        match request.ResponseFormat with
        | Some(ResponseFormat.JsonSchema(_, schema, _)) ->
            genConfig["responseMimeType"] <- "application/json"
            genConfig["responseSchema"] <- HttpAdapterHelpers.tryParseJsonObject schema ({| ``type`` = "object" |} :> obj)
        | Some ResponseFormat.JsonObject ->
            genConfig["responseMimeType"] <- "application/json"
        | _ -> ()

        if genConfig.Count > 0 then
            bodyObj["generationConfig"] <- genConfig

        match request.ToolChoice with
        | Some choice ->
            let fc = Dictionary<string, obj>()
            match choice with
            | ToolChoice.Auto -> fc["mode"] <- "AUTO"
            | ToolChoice.None -> fc["mode"] <- "NONE"
            | ToolChoice.Required -> fc["mode"] <- "ANY"
            | ToolChoice.Named name ->
                fc["mode"] <- "ANY"
                fc["allowedFunctionNames"] <- [| name |]
            bodyObj["toolConfig"] <- {| functionCallingConfig = fc |}
        | None -> ()

        // Forward unknown provider options as top-level Gemini request fields.
        match tryGetGeminiOptions request with
        | Some opts ->
            for KeyValue(k, v) in opts do
                if not (bodyObj.ContainsKey(k)) then
                    bodyObj[k] <- HttpAdapterHelpers.toProviderOptionPayload v
        | None -> ()

        bodyObj

    let buildHttpRequest (url: string) (request: Request) =
        let body = JsonSerializer.Serialize(buildBody request)
        let httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        httpReq.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        httpReq

    interface IProviderAdapter with
        member _.ProviderId = "gemini"
        member _.Initialize() = async.Return()
        member _.Close() =
            async {
                client.Dispose()
            }
        member _.SupportsToolChoice() = true

        member _.Complete(request: Request) =
            let model = if request.Model = "" then "gemini-2.5-flash-preview-05-20" else request.Model
            let url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}"
            let httpReq = buildHttpRequest url request
            let httpResp, respBody = HttpAdapterHelpers.sendAndReadString client request httpReq

            if not httpResp.IsSuccessStatusCode then
                let err = ErrorMapping.classifyHttpResponse httpResp respBody
                raise err

            HttpAdapterHelpers.responseFromGemini model respBody httpResp

        member _.Stream(request: Request) =
            let model = if request.Model = "" then "gemini-2.5-flash-preview-05-20" else request.Model
            let url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}"

            seq {
                let httpReq = buildHttpRequest url request
                let httpResp, cts = HttpAdapterHelpers.sendForStream client request httpReq
                use _cts = cts

                if not httpResp.IsSuccessStatusCode then
                    let body = httpResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    let err = ErrorMapping.classifyHttpResponse httpResp body
                    raise err

                use stream = httpResp.Content.ReadAsStream(cts.Token)
                use reader = new StreamReader(stream)

                let rawEvents = ResizeArray<string>()
                let mutable accumulatedText = ""
                let mutable textStarted = false
                let toolCalls = ResizeArray<ToolCallData>()
                let seenToolKeys = HashSet<string>()
                let mutable usage = Usage.Zero
                let mutable finishReason = Stop "STOP"

                yield StreamStart

                for (_eventName, data) in HttpAdapterHelpers.parseSse reader do
                    if data <> "[DONE]" then
                        rawEvents.Add(data)

                        let doc = JsonDocument.Parse(data)
                        let root = doc.RootElement

                        match HttpAdapterHelpers.tryGetProperty "candidates" root with
                        | Some candidates when candidates.GetArrayLength() > 0 ->
                            let first = candidates.[0]
                            let rawFinish = HttpAdapterHelpers.tryGetString "finishReason" first |> Option.defaultValue ""

                            match HttpAdapterHelpers.tryGetProperty "content" first |> Option.bind (HttpAdapterHelpers.tryGetProperty "parts") with
                            | Some parts ->
                                let currentText =
                                    parts.EnumerateArray()
                                    |> Seq.choose (fun p -> HttpAdapterHelpers.tryGetProperty "text" p |> Option.map (fun t -> t.GetString()))
                                    |> String.concat ""

                                if currentText.Length > accumulatedText.Length then
                                    let delta = currentText.Substring(accumulatedText.Length)
                                    if delta <> "" then
                                        if not textStarted then
                                            textStarted <- true
                                            yield TextStart "text-0"
                                        accumulatedText <- currentText
                                        yield TextDelta(Some "text-0", delta)

                                for part in parts.EnumerateArray() do
                                    match HttpAdapterHelpers.tryGetProperty "functionCall" part with
                                    | Some fc ->
                                        let name = HttpAdapterHelpers.tryGetString "name" fc |> Option.defaultValue "unknown_tool"
                                        let argsText =
                                            match HttpAdapterHelpers.tryGetProperty "args" fc with
                                            | Some args -> args.GetRawText()
                                            | None -> "{}"
                                        let key = name + "|" + argsText
                                        if seenToolKeys.Add(key) then
                                            let metadata =
                                                match HttpAdapterHelpers.tryGetString "thoughtSignature" part with
                                                | Some ts -> Map.ofList [ "thoughtSignature", ts ]
                                                | None -> Map.empty
                                            let tc: ToolCallData =
                                                { Id = "gemini-tc-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                                                  Name = name
                                                  Arguments = argsText
                                                  Metadata = metadata }
                                            toolCalls.Add(tc)
                                            yield ToolCallStart tc
                                            yield ToolCallEnd tc
                                    | None -> ()
                            | None -> ()

                            finishReason <- HttpAdapterHelpers.mapGeminiFinishReason rawFinish (toolCalls.Count > 0)
                        | _ -> ()

                        match HttpAdapterHelpers.tryGetProperty "usageMetadata" root with
                        | Some u ->
                            usage <-
                                { InputTokens = HttpAdapterHelpers.tryGetProperty "promptTokenCount" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue usage.InputTokens
                                  OutputTokens = HttpAdapterHelpers.tryGetProperty "candidatesTokenCount" u |> Option.map (fun v -> v.GetInt32()) |> Option.defaultValue usage.OutputTokens
                                  ReasoningTokens = HttpAdapterHelpers.tryGetProperty "thoughtsTokenCount" u |> Option.map (fun v -> v.GetInt32())
                                  CacheReadTokens = HttpAdapterHelpers.tryGetProperty "cachedContentTokenCount" u |> Option.map (fun v -> v.GetInt32())
                                  CacheWriteTokens = usage.CacheWriteTokens }
                        | None -> ()

                if textStarted then
                    yield TextEnd "text-0"

                let contentParts =
                    [ if accumulatedText <> "" then yield ContentPart.Text accumulatedText
                      for tc in toolCalls do yield ContentPart.ToolCall tc ]

                let response =
                    { Id = "gemini-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                      Model = model
                      Provider = "gemini"
                      Message = { Role = Assistant; Content = (if contentParts.IsEmpty then [ ContentPart.Text "" ] else contentParts); Name = None; ToolCallId = None }
                      FinishReason = finishReason
                      Usage = usage
                      ResponseId = None
                      Raw = Some(HttpAdapterHelpers.toRawElement (String.concat "\n" rawEvents))
                      Warnings = []
                      RateLimit = HttpAdapterHelpers.parseGeminiRateLimit httpResp }

                yield Finish(finishReason, Some usage, Some response)
            }
