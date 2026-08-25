namespace UnifiedLlm

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type CacheKey = private CacheKey of string

type CachedLlmResponse =
    { Response: Response
      StoredAt: DateTimeOffset
      Metadata: Map<string, string> }

type private CacheToolCallPayload =
    { Id: string
      Name: string
      Arguments: string
      Metadata: Map<string, string> }

type private CacheUsagePayload =
    { InputTokens: int
      OutputTokens: int
      ReasoningTokens: int option
      CacheReadTokens: int option
      CacheWriteTokens: int option }

type private PersistedCacheEntry =
    { Id: string
      Model: string
      Provider: string
      Text: string
      ContentJson: string option
      ToolCalls: CacheToolCallPayload list
      FinishReasonTag: string
      FinishReasonRaw: string
      Usage: CacheUsagePayload
      ResponseId: string option
      Warnings: string list
      StoredAt: DateTimeOffset
      Metadata: Map<string, string> }

type CacheConfig =
    { MaxEntries: int
      TimeToLive: TimeSpan
      PersistencePath: string option }

    static member Default =
        { MaxEntries = 1000
          TimeToLive = TimeSpan.FromHours(1.0)
          PersistencePath = None }

    static member Disabled =
        { MaxEntries = 0
          TimeToLive = TimeSpan.Zero
          PersistencePath = None }

type CacheStore =
    { TryGetLlm: CacheKey -> Async<CachedLlmResponse option>
      PutLlm: CacheKey -> CachedLlmResponse -> Async<unit>
      TryGetTool: CacheKey -> Async<ToolResultData option>
      PutTool: CacheKey -> ToolResultData -> Async<unit>
      Remove: CacheKey -> Async<unit>
      Count: unit -> int
      Clear: unit -> unit }

module CacheKey =

    let value (CacheKey key) = key

    let private writeString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let private writeFloat (writer: Utf8JsonWriter) (name: string) (value: float option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull(name)

    let private writeInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull(name)

    let private writeBytes (writer: Utf8JsonWriter) (name: string) (value: byte array option) =
        match value with
        | Some bytes -> writer.WriteString(name, Convert.ToBase64String(bytes))
        | None -> writer.WriteNull(name)

    let rec private writeCanonicalJson (writer: Utf8JsonWriter) (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            writer.WriteStartObject()

            value.EnumerateObject()
            |> Seq.sortBy _.Name
            |> Seq.iter (fun property ->
                writer.WritePropertyName(property.Name)
                writeCanonicalJson writer property.Value)

            writer.WriteEndObject()
        | JsonValueKind.Array ->
            writer.WriteStartArray()
            value.EnumerateArray() |> Seq.iter (writeCanonicalJson writer)
            writer.WriteEndArray()
        | _ -> value.WriteTo(writer)

    let private writeProviderOptions (writer: Utf8JsonWriter) (options: Map<string, obj> option) =
        match options with
        | None -> writer.WriteNull("providerOptions")
        | Some values ->
            writer.WriteStartObject("providerOptions")

            for key, value in values |> Map.toSeq |> Seq.sortBy fst do
                // These typed request features are written separately below.
                if key <> "unified_llm.custom_tools" && key <> "unified_llm.gemini_code_execution" then
                    writer.WritePropertyName(key)

                    if isNull value then
                        writer.WriteNullValue()
                    else
                        try
                            JsonSerializer.SerializeToElement(value, value.GetType())
                            |> writeCanonicalJson writer
                        with :? NotSupportedException ->
                            writer.WriteStartObject()
                            writer.WriteString("type", value.GetType().FullName)
                            writer.WriteString("value", value.ToString())
                            writer.WriteEndObject()

            writer.WriteEndObject()

    let private writeMetadata (writer: Utf8JsonWriter) (metadata: Map<string, string> option) =
        match metadata with
        | None -> writer.WriteNull("metadata")
        | Some values ->
            writer.WriteStartObject("metadata")

            for key, value in values |> Map.toSeq |> Seq.sortBy fst do
                writer.WriteString(key, value)

            writer.WriteEndObject()

    let private writeStringList (writer: Utf8JsonWriter) (name: string) (values: string list option) =
        match values with
        | None -> writer.WriteNull(name)
        | Some items ->
            writer.WriteStartArray(name)

            for item in items do
                writer.WriteStringValue(item)

            writer.WriteEndArray()

    let private writeToolChoice (writer: Utf8JsonWriter) (toolChoice: ToolChoice option) =
        match toolChoice with
        | Option.None -> writer.WriteNull("toolChoice")
        | Some ToolChoice.Auto -> writer.WriteString("toolChoice", "auto")
        | Some ToolChoice.None -> writer.WriteString("toolChoice", "none")
        | Some ToolChoice.Required -> writer.WriteString("toolChoice", "required")
        | Some(ToolChoice.Named name) ->
            writer.WriteStartObject("toolChoice")
            writer.WriteString("type", "named")
            writer.WriteString("name", name)
            writer.WriteEndObject()

    let private writeResponseFormat (writer: Utf8JsonWriter) (responseFormat: ResponseFormat option) =
        match responseFormat with
        | None -> writer.WriteNull("responseFormat")
        | Some ResponseFormat.Text -> writer.WriteString("responseFormat", "text")
        | Some ResponseFormat.JsonObject -> writer.WriteString("responseFormat", "json_object")
        | Some(ResponseFormat.JsonSchema(name, schema, strict)) ->
            writer.WriteStartObject("responseFormat")
            writer.WriteString("type", "json_schema")
            writer.WriteString("name", name)
            writer.WriteString("schema", schema)
            writer.WriteBoolean("strict", strict)
            writer.WriteEndObject()

    let private writeToolDefinitions (writer: Utf8JsonWriter) (tools: ToolDefinition list option) =
        match tools with
        | None -> writer.WriteNull("tools")
        | Some definitions ->
            writer.WriteStartArray("tools")

            for definition in definitions do
                writer.WriteStartObject()
                writer.WriteString("description", definition.Description)
                writer.WriteString("name", definition.Name)
                writer.WriteString("parameters", definition.Parameters)
                writer.WriteEndObject()

            writer.WriteEndArray()

    let private writeCustomToolDefinitions (writer: Utf8JsonWriter) (tools: CustomToolDefinition list) =
        writer.WriteStartArray("customTools")

        for definition in tools do
            writer.WriteStartObject()
            writer.WriteString("description", definition.Description)
            writer.WriteString("name", definition.Name)

            match definition.Format with
            | CustomToolFormat.FreeText -> writer.WriteString("format", "text")
            | CustomToolFormat.Grammar(syntax, grammarDefinition) ->
                writer.WriteString("format", "grammar")
                writer.WriteString("syntax", syntax)
                writer.WriteString("definition", grammarDefinition)

            writer.WriteEndObject()

        writer.WriteEndArray()

    let private writeContentPart (writer: Utf8JsonWriter) (part: ContentPart) =
        writer.WriteStartObject()

        match part with
        | Text text ->
            writer.WriteString("kind", "text")
            writer.WriteString("text", text)
        | Image image ->
            writer.WriteString("kind", "image")
            writeString writer "url" image.Url
            writeString writer "filePath" image.FilePath
            writeString writer "mediaType" image.MediaType
            writeBytes writer "data" image.Data
        | Audio audio ->
            writer.WriteString("kind", "audio")
            writeString writer "url" audio.Url
            writeString writer "mediaType" audio.MediaType
            writeBytes writer "data" audio.Data
        | Document document ->
            writer.WriteString("kind", "document")
            writeString writer "url" document.Url
            writeString writer "fileName" document.FileName
            writeString writer "mediaType" document.MediaType
            writeBytes writer "data" document.Data
        | ToolCall toolCall ->
            writer.WriteString("kind", "tool_call")
            writer.WriteString("arguments", toolCall.Arguments)
            writer.WriteString("id", toolCall.Id)
            writer.WriteStartObject("metadata")

            for key, value in toolCall.Metadata |> Map.toSeq |> Seq.sortBy fst do
                writer.WriteString(key, value)

            writer.WriteEndObject()
            writer.WriteString("name", toolCall.Name)
        | ToolResult toolResult ->
            writer.WriteString("kind", "tool_result")
            writer.WriteString("content", toolResult.Content)
            writer.WriteBoolean("isError", toolResult.IsError)
            writer.WriteString("toolCallId", toolResult.ToolCallId)
            writeBytes writer "imageData" toolResult.ImageData
            writeString writer "imageMediaType" toolResult.ImageMediaType
        | Thinking thinking ->
            writer.WriteString("kind", "thinking")
            writer.WriteBoolean("redacted", thinking.Redacted)
            writeString writer "signature" thinking.Signature
            writer.WriteString("text", thinking.Text)
        | CustomToolCall call ->
            writer.WriteString("kind", "custom_tool_call")
            writer.WriteString("id", call.Id)
            writer.WriteString("name", call.Name)
            writer.WriteString("input", call.Input)
        | CustomToolResult result ->
            writer.WriteString("kind", "custom_tool_result")
            writer.WriteString("toolCallId", result.ToolCallId)
            writer.WriteString("output", result.Output)
        | CodeExecution execution ->
            writer.WriteString("kind", "code_execution")
            writer.WriteString("language", execution.Language)
            writer.WriteString("code", execution.Code)
        | CodeExecutionResult result ->
            writer.WriteString("kind", "code_execution_result")
            writer.WriteString("outcome", result.Outcome)
            writer.WriteString("output", result.Output)

        writer.WriteEndObject()

    let private writeMessages (writer: Utf8JsonWriter) (messages: Message list) =
        writer.WriteStartArray("messages")

        for message in messages do
            writer.WriteStartObject()
            writeString writer "name" message.Name

            writer.WriteString(
                "role",
                match message.Role with
                | Role.System -> "system"
                | Role.User -> "user"
                | Role.Assistant -> "assistant"
                | Role.Tool -> "tool"
                | Role.Developer -> "developer"
            )

            writer.WriteStartArray("content")

            for part in message.Content do
                writeContentPart writer part

            writer.WriteEndArray()
            writeString writer "toolCallId" message.ToolCallId
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private requestBytes (request: Request) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("model", request.Model)
        writeString writer "provider" request.Provider
        writeMessages writer request.Messages
        writeString writer "prompt" request.Prompt
        writeToolDefinitions writer request.Tools
        writeCustomToolDefinitions writer request.CustomTools
        writer.WriteBoolean("codeExecution", request.CodeExecutionEnabled)
        writeToolChoice writer request.ToolChoice
        writeResponseFormat writer request.ResponseFormat
        writeFloat writer "temperature" request.Temperature
        writeFloat writer "topP" request.TopP
        writeInt writer "maxTokens" request.MaxTokens
        writeString writer "reasoningEffort" request.ReasoningEffort
        writeString writer "previousResponseId" request.PreviousResponseId
        writeStringList writer "stopSequences" request.StopSequences
        writeMetadata writer request.Metadata
        writeProviderOptions writer request.ProviderOptions
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let fromRequest (request: Request) =
        use sha256 = SHA256.Create()
        let hash = sha256.ComputeHash(requestBytes request)
        CacheKey(Convert.ToHexString(hash))

    let fromToolCall (toolName: string) (arguments: string) (workingDir: string) =
        use sha256 = SHA256.Create()
        let bytes = Encoding.UTF8.GetBytes($"{toolName}\n{arguments}\n{workingDir}")
        let hash = sha256.ComputeHash(bytes)
        CacheKey(Convert.ToHexString(hash))

module CacheStore =

    let private writeString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let private writeInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull(name)

    let private writeBytes (writer: Utf8JsonWriter) (name: string) (value: byte array option) =
        match value with
        | Some bytes -> writer.WriteString(name, Convert.ToBase64String(bytes))
        | None -> writer.WriteNull(name)

    let private writeStringMap (writer: Utf8JsonWriter) (name: string) (values: Map<string, string>) =
        writer.WriteStartObject(name)

        for key, value in values |> Map.toSeq |> Seq.sortBy fst do
            writer.WriteString(key, value)

        writer.WriteEndObject()

    let private writeStringList (writer: Utf8JsonWriter) (name: string) (values: string list) =
        writer.WriteStartArray(name)

        for item in values do
            writer.WriteStringValue(item)

        writer.WriteEndArray()

    let private serializeContent (content: ContentPart list) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartArray()

        for part in content do
            writer.WriteStartObject()

            match part with
            | Text text ->
                writer.WriteString("kind", "text")
                writer.WriteString("text", text)
            | Image image ->
                writer.WriteString("kind", "image")
                writeString writer "url" image.Url
                writeString writer "filePath" image.FilePath
                writeString writer "mediaType" image.MediaType
                writeBytes writer "data" image.Data
            | Audio audio ->
                writer.WriteString("kind", "audio")
                writeString writer "url" audio.Url
                writeString writer "mediaType" audio.MediaType
                writeBytes writer "data" audio.Data
            | Document document ->
                writer.WriteString("kind", "document")
                writeString writer "url" document.Url
                writeString writer "mediaType" document.MediaType
                writeString writer "fileName" document.FileName
                writeBytes writer "data" document.Data
            | ToolCall call ->
                writer.WriteString("kind", "tool_call")
                writer.WriteString("id", call.Id)
                writer.WriteString("name", call.Name)
                writer.WriteString("arguments", call.Arguments)
                writeStringMap writer "metadata" call.Metadata
            | ToolResult result ->
                writer.WriteString("kind", "tool_result")
                writer.WriteString("toolCallId", result.ToolCallId)
                writer.WriteString("content", result.Content)
                writer.WriteBoolean("isError", result.IsError)
                writeString writer "imageMediaType" result.ImageMediaType
                writeBytes writer "imageData" result.ImageData
            | Thinking thinking ->
                writer.WriteString("kind", "thinking")
                writer.WriteString("text", thinking.Text)
                writeString writer "signature" thinking.Signature
                writer.WriteBoolean("redacted", thinking.Redacted)
            | CustomToolCall call ->
                writer.WriteString("kind", "custom_tool_call")
                writer.WriteString("id", call.Id)
                writer.WriteString("name", call.Name)
                writer.WriteString("input", call.Input)
            | CustomToolResult result ->
                writer.WriteString("kind", "custom_tool_result")
                writer.WriteString("toolCallId", result.ToolCallId)
                writer.WriteString("output", result.Output)
            | CodeExecution execution ->
                writer.WriteString("kind", "code_execution")
                writer.WriteString("language", execution.Language)
                writer.WriteString("code", execution.Code)
            | CodeExecutionResult result ->
                writer.WriteString("kind", "code_execution_result")
                writer.WriteString("outcome", result.Outcome)
                writer.WriteString("output", result.Output)

            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private serializePersisted (entry: PersistedCacheEntry) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("id", entry.Id)
        writer.WriteString("model", entry.Model)
        writer.WriteString("provider", entry.Provider)
        writer.WriteString("text", entry.Text)
        writeString writer "contentJson" entry.ContentJson
        writer.WriteStartArray("toolCalls")

        for toolCall in entry.ToolCalls do
            writer.WriteStartObject()
            writer.WriteString("id", toolCall.Id)
            writer.WriteString("name", toolCall.Name)
            writer.WriteString("arguments", toolCall.Arguments)
            writeStringMap writer "metadata" toolCall.Metadata
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteString("finishReasonTag", entry.FinishReasonTag)
        writer.WriteString("finishReasonRaw", entry.FinishReasonRaw)
        writer.WriteStartObject("usage")
        writer.WriteNumber("inputTokens", entry.Usage.InputTokens)
        writer.WriteNumber("outputTokens", entry.Usage.OutputTokens)
        writeInt writer "reasoningTokens" entry.Usage.ReasoningTokens
        writeInt writer "cacheReadTokens" entry.Usage.CacheReadTokens
        writeInt writer "cacheWriteTokens" entry.Usage.CacheWriteTokens
        writer.WriteEndObject()
        writeString writer "responseId" entry.ResponseId
        writeStringList writer "warnings" entry.Warnings
        writer.WriteString("storedAt", entry.StoredAt)
        writeStringMap writer "metadata" entry.Metadata
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray()

    let private tryGetProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private getString (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | Some value -> failwith $"Expected '{name}' to be a string but was {value.ValueKind}"
        | None -> failwith $"Missing required property '{name}'"

    let private getOptionalString (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> None
        | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | Some value -> failwith $"Expected '{name}' to be a string or null but was {value.ValueKind}"
        | None -> None

    let private getInt (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | Some value -> failwith $"Expected '{name}' to be a number but was {value.ValueKind}"
        | None -> failwith $"Missing required property '{name}'"

    let private getOptionalInt (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> None
        | Some value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
        | Some value -> failwith $"Expected '{name}' to be a number or null but was {value.ValueKind}"
        | None -> None

    let private getOptionalBytes (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Null -> None
        | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetBytesFromBase64())
        | Some value -> failwith $"Expected '{name}' to be a base64 string or null but was {value.ValueKind}"
        | None -> None

    let private getStringMap (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Object ->
            value.EnumerateObject()
            |> Seq.map (fun prop -> prop.Name, prop.Value.GetString())
            |> Map.ofSeq
        | Some value -> failwith $"Expected '{name}' to be an object but was {value.ValueKind}"
        | None -> Map.empty

    let private getStringList (name: string) (root: JsonElement) =
        match tryGetProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.map (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    item.GetString()
                else
                    failwith $"Expected '{name}' array entries to be strings")
            |> Seq.toList
        | Some value -> failwith $"Expected '{name}' to be an array but was {value.ValueKind}"
        | None -> []

    let private deserializeContent (json: string) =
        use doc = JsonDocument.Parse(json)

        doc.RootElement.EnumerateArray()
        |> Seq.choose (fun item ->
            match getString "kind" item with
            | "text" -> Some(Text(getString "text" item))
            | "image" ->
                Some(
                    Image
                        { Url = getOptionalString "url" item
                          Data = getOptionalBytes "data" item
                          FilePath = getOptionalString "filePath" item
                          MediaType = getOptionalString "mediaType" item }
                )
            | "audio" ->
                Some(
                    Audio
                        { Url = getOptionalString "url" item
                          Data = getOptionalBytes "data" item
                          MediaType = getOptionalString "mediaType" item }
                )
            | "document" ->
                Some(
                    Document
                        { Url = getOptionalString "url" item
                          Data = getOptionalBytes "data" item
                          MediaType = getOptionalString "mediaType" item
                          FileName = getOptionalString "fileName" item }
                )
            | "tool_call" ->
                Some(
                    ToolCall
                        { Id = getString "id" item
                          Name = getString "name" item
                          Arguments = getString "arguments" item
                          Metadata = getStringMap "metadata" item }
                )
            | "tool_result" ->
                Some(
                    ToolResult
                        { ToolCallId = getString "toolCallId" item
                          Content = getString "content" item
                          IsError =
                            tryGetProperty "isError" item
                            |> Option.map _.GetBoolean()
                            |> Option.defaultValue false
                          ImageData = getOptionalBytes "imageData" item
                          ImageMediaType = getOptionalString "imageMediaType" item }
                )
            | "thinking" ->
                Some(
                    Thinking
                        { Text = getString "text" item
                          Signature = getOptionalString "signature" item
                          Redacted =
                            tryGetProperty "redacted" item
                            |> Option.map _.GetBoolean()
                            |> Option.defaultValue false }
                )
            | "custom_tool_call" ->
                Some(
                    CustomToolCall
                        { Id = getString "id" item
                          Name = getString "name" item
                          Input = getString "input" item }
                )
            | "custom_tool_result" ->
                Some(
                    CustomToolResult
                        { ToolCallId = getString "toolCallId" item
                          Output = getString "output" item }
                )
            | "code_execution" ->
                Some(
                    CodeExecution
                        { Language = getString "language" item
                          Code = getString "code" item }
                )
            | "code_execution_result" ->
                Some(
                    CodeExecutionResult
                        { Outcome = getString "outcome" item
                          Output = getString "output" item }
                )
            | _ -> None)
        |> Seq.toList

    let private deserializePersisted (bytes: byte array) =
        use doc = JsonDocument.Parse(bytes)
        let root = doc.RootElement

        let usageRoot =
            match tryGetProperty "usage" root with
            | Some value when value.ValueKind = JsonValueKind.Object -> value
            | Some value -> failwith $"Expected 'usage' to be an object but was {value.ValueKind}"
            | None -> failwith "Missing required property 'usage'"

        let toolCalls =
            match tryGetProperty "toolCalls" root with
            | Some value when value.ValueKind = JsonValueKind.Array ->
                value.EnumerateArray()
                |> Seq.map (fun item ->
                    { Id = getString "id" item
                      Name = getString "name" item
                      Arguments = getString "arguments" item
                      Metadata = getStringMap "metadata" item })
                |> Seq.toList
            | Some value -> failwith $"Expected 'toolCalls' to be an array but was {value.ValueKind}"
            | None -> []

        { Id = getString "id" root
          Model = getString "model" root
          Provider = getString "provider" root
          Text = getString "text" root
          ContentJson = getOptionalString "contentJson" root
          ToolCalls = toolCalls
          FinishReasonTag = getString "finishReasonTag" root
          FinishReasonRaw = getString "finishReasonRaw" root
          Usage =
            { InputTokens = getInt "inputTokens" usageRoot
              OutputTokens = getInt "outputTokens" usageRoot
              ReasoningTokens = getOptionalInt "reasoningTokens" usageRoot
              CacheReadTokens = getOptionalInt "cacheReadTokens" usageRoot
              CacheWriteTokens = getOptionalInt "cacheWriteTokens" usageRoot }
          ResponseId = getOptionalString "responseId" root
          Warnings = getStringList "warnings" root
          StoredAt = DateTimeOffset.Parse(getString "storedAt" root)
          Metadata = getStringMap "metadata" root }

    let private toPersisted (entry: CachedLlmResponse) =
        let finishReasonTag, finishReasonRaw =
            match entry.Response.FinishReason with
            | Stop raw -> "stop", raw
            | ToolCalls raw -> "tool_calls", raw
            | Length raw -> "length", raw
            | ContentFilter raw -> "content_filter", raw
            | Error raw -> "error", raw
            | Other raw -> "other", raw

        { Id = entry.Response.Id
          Model = entry.Response.Model
          Provider = entry.Response.Provider
          Text = entry.Response.Text
          ContentJson = Some(serializeContent entry.Response.Message.Content)
          ToolCalls =
            entry.Response.ToolCalls
            |> List.map (fun toolCall ->
                { Id = toolCall.Id
                  Name = toolCall.Name
                  Arguments = toolCall.Arguments
                  Metadata = toolCall.Metadata })
          FinishReasonTag = finishReasonTag
          FinishReasonRaw = finishReasonRaw
          Usage =
            { InputTokens = entry.Response.Usage.InputTokens
              OutputTokens = entry.Response.Usage.OutputTokens
              ReasoningTokens = entry.Response.Usage.ReasoningTokens
              CacheReadTokens = entry.Response.Usage.CacheReadTokens
              CacheWriteTokens = entry.Response.Usage.CacheWriteTokens }
          ResponseId = entry.Response.ResponseId
          Warnings = entry.Response.Warnings
          StoredAt = entry.StoredAt
          Metadata = entry.Metadata }

    let private ofPersisted (entry: PersistedCacheEntry) =
        let finishReason =
            match entry.FinishReasonTag with
            | "stop" -> Stop entry.FinishReasonRaw
            | "tool_calls" -> ToolCalls entry.FinishReasonRaw
            | "length" -> Length entry.FinishReasonRaw
            | "content_filter" -> ContentFilter entry.FinishReasonRaw
            | "error" -> Error entry.FinishReasonRaw
            | _ -> Other entry.FinishReasonRaw

        let toolCalls =
            entry.ToolCalls
            |> List.map (fun toolCall ->
                ToolCall
                    { Id = toolCall.Id
                      Name = toolCall.Name
                      Arguments = toolCall.Arguments
                      Metadata = toolCall.Metadata })

        let content =
            match entry.ContentJson with
            | Some json -> deserializeContent json
            | None ->
                [ if entry.Text <> "" then
                      yield Text entry.Text
                  yield! toolCalls ]

        { Response =
            { Id = entry.Id
              Model = entry.Model
              Provider = entry.Provider
              Message =
                { Role = Assistant
                  Content = if List.isEmpty content then [ Text "" ] else content
                  Name = None
                  ToolCallId = None }
              FinishReason = finishReason
              Usage =
                { InputTokens = entry.Usage.InputTokens
                  OutputTokens = entry.Usage.OutputTokens
                  ReasoningTokens = entry.Usage.ReasoningTokens
                  CacheReadTokens = entry.Usage.CacheReadTokens
                  CacheWriteTokens = entry.Usage.CacheWriteTokens }
              ResponseId = entry.ResponseId
              Raw = None
              Warnings = entry.Warnings
              RateLimit = None }
          StoredAt = entry.StoredAt
          Metadata = entry.Metadata }

    let fileSystem (config: CacheConfig) : CacheStore =
        let llmCache = ConcurrentDictionary<string, CachedLlmResponse>()
        let toolCache = ConcurrentDictionary<string, DateTimeOffset * ToolResultData>()
        let accessTimes = ConcurrentDictionary<string, DateTimeOffset>()
        let isEnabled = config.MaxEntries > 0 && config.TimeToLive > TimeSpan.Zero

        let llmPath key =
            config.PersistencePath
            |> Option.map (fun root -> Path.Combine(root, "llm", key + ".json"))

        let isFresh storedAt =
            DateTimeOffset.UtcNow - storedAt < config.TimeToLive

        let writeAtomically (path: string) (content: byte array) =
            let dir = Path.GetDirectoryName(path)

            if not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            let tempPath = path + ".tmp." + Guid.NewGuid().ToString("N")
            File.WriteAllBytes(tempPath, content)
            File.Move(tempPath, path, true)

        let evictIfNeeded () =
            if isEnabled && llmCache.Count > config.MaxEntries then
                let overflow = llmCache.Count - config.MaxEntries

                let victims =
                    accessTimes
                    |> Seq.sortBy (fun pair -> pair.Value)
                    |> Seq.truncate overflow
                    |> Seq.map (fun pair -> pair.Key)
                    |> Seq.toList

                for victim in victims do
                    llmCache.TryRemove(victim) |> ignore
                    accessTimes.TryRemove(victim) |> ignore

                    match llmPath victim with
                    | Some path when File.Exists(path) -> File.Delete(path)
                    | _ -> ()

        let removeExpiredTool key =
            match toolCache.TryGetValue(key) with
            | true, (storedAt, _) when not (isFresh storedAt) -> toolCache.TryRemove(key) |> ignore
            | _ -> ()

        { TryGetLlm =
            fun key ->
                async {
                    if not isEnabled then
                        return None
                    else
                        let rawKey = CacheKey.value key
                        accessTimes[rawKey] <- DateTimeOffset.UtcNow

                        match llmCache.TryGetValue(rawKey) with
                        | true, entry when isFresh entry.StoredAt -> return Some entry
                        | true, _ ->
                            llmCache.TryRemove(rawKey) |> ignore
                            accessTimes.TryRemove(rawKey) |> ignore

                            match llmPath rawKey with
                            | Some path when File.Exists(path) -> File.Delete(path)
                            | _ -> ()

                            return None
                        | false, _ ->
                            match llmPath rawKey with
                            | Some path when File.Exists(path) ->
                                try
                                    let bytes = File.ReadAllBytes(path)
                                    let entry = deserializePersisted bytes |> ofPersisted

                                    if isFresh entry.StoredAt then
                                        llmCache[rawKey] <- entry
                                        return Some entry
                                    else
                                        File.Delete(path)
                                        return None
                                with _ ->
                                    try
                                        File.Delete(path)
                                    with _ ->
                                        ()

                                    return None
                            | _ -> return None
                }
          PutLlm =
            fun key entry ->
                async {
                    if isEnabled then
                        let rawKey = CacheKey.value key
                        llmCache[rawKey] <- entry
                        accessTimes[rawKey] <- DateTimeOffset.UtcNow
                        evictIfNeeded ()

                        match llmPath rawKey with
                        | Some path ->
                            let bytes = entry |> toPersisted |> serializePersisted
                            writeAtomically path bytes
                        | None -> ()
                }
          TryGetTool =
            fun key ->
                async {
                    if not isEnabled then
                        return None
                    else
                        let rawKey = CacheKey.value key
                        removeExpiredTool rawKey

                        match toolCache.TryGetValue(rawKey) with
                        | true, (_, result) -> return Some result
                        | false, _ -> return None
                }
          PutTool =
            fun key result ->
                async {
                    if isEnabled then
                        toolCache[CacheKey.value key] <- DateTimeOffset.UtcNow, result
                }
          Remove =
            fun key ->
                async {
                    let rawKey = CacheKey.value key
                    llmCache.TryRemove(rawKey) |> ignore
                    toolCache.TryRemove(rawKey) |> ignore
                    accessTimes.TryRemove(rawKey) |> ignore

                    match llmPath rawKey with
                    | Some path when File.Exists(path) -> File.Delete(path)
                    | _ -> ()
                }
          Count = fun () -> llmCache.Count + toolCache.Count
          Clear =
            fun () ->
                llmCache.Clear()
                toolCache.Clear()
                accessTimes.Clear()

                match config.PersistencePath with
                | Some root when Directory.Exists(root) -> Directory.Delete(root, true)
                | _ -> () }

module Caching =

    let replayStreamFromCachedResponse (response: Response) : StreamEvent seq =
        seq {
            yield StreamStart

            yield
                ResponseCreated
                    { Id = response.ResponseId |> Option.orElse (Some response.Id)
                      Model = Some response.Model
                      Provider = response.Provider
                      Status = "cached"
                      Raw = None }

            if response.Text <> "" then
                yield TextStart "cached-text"
                yield TextDelta(Some "cached-text", response.Text)
                yield TextEnd "cached-text"

            for toolCall in response.ToolCalls do
                yield ToolCallStart toolCall
                yield ToolCallEnd toolCall

            for part in response.Message.Content do
                match part with
                | CustomToolCall call ->
                    yield CustomToolCallStart call
                    yield CustomToolCallEnd call
                | CodeExecution execution -> yield CodeExecutionEvent execution
                | CodeExecutionResult result -> yield CodeExecutionResultEvent result
                | Audio audio when audio.Data.IsSome ->
                    yield
                        AudioDelta
                            { Data = audio.Data |> Option.defaultValue Array.empty
                              Transcript = None
                              Sequence = None
                              MediaType = audio.MediaType
                              Final = true }
                | _ -> ()

            yield
                UsageDelta
                    { Delta = response.Usage
                      Total = Some response.Usage }

            yield StepFinish(0, Some response)
            yield Finish(response.FinishReason, Some response.Usage, Some response)
        }
