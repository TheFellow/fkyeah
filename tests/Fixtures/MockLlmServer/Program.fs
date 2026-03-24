open System
open System.IO
open System.Net
open System.Text
open System.Text.Json

type Config =
    { Port: int
      RecordDir: string
      Scenario: string }

let parseArgs (args: string array) =
    let rec loop index config =
        if index >= args.Length then
            config
        else
            match args[index] with
            | "--port" -> loop (index + 2) { config with Port = int args[index + 1] }
            | "--record-dir" -> loop (index + 2) { config with RecordDir = args[index + 1] }
            | "--scenario" -> loop (index + 2) { config with Scenario = args[index + 1] }
            | arg -> failwith $"Unknown argument: {arg}"

    loop
        0
        { Port = 8787
          RecordDir = Path.Combine(Path.GetTempPath(), "mock-llm-records")
          Scenario = "default" }

let config = parseArgs ((Environment.GetCommandLineArgs())[1..])
Directory.CreateDirectory(config.RecordDir) |> ignore

let counterLock = obj ()
let mutable requestCount = 0

let nextRequestIndex () =
    lock counterLock (fun () ->
        requestCount <- requestCount + 1
        requestCount)

let tryGetProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

let tryGetString (name: string) (element: JsonElement) =
    tryGetProperty name element
    |> Option.bind (fun value ->
        if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)

let readBody (request: HttpListenerRequest) =
    use reader = new StreamReader(request.InputStream, request.ContentEncoding)
    reader.ReadToEnd()

let hasStreamFlag (body: string) =
    try
        use doc = JsonDocument.Parse(body)
        match tryGetProperty "stream" doc.RootElement with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | _ -> false
    with _ ->
        false

let writeJson (response: HttpListenerResponse) (statusCode: int) (payload: obj) =
    let body = JsonSerializer.Serialize(payload)
    let bytes = Encoding.UTF8.GetBytes(body)
    response.StatusCode <- statusCode
    response.ContentType <- "application/json"
    response.ContentEncoding <- Encoding.UTF8
    response.OutputStream.Write(bytes, 0, bytes.Length)
    response.OutputStream.Close()

let writeSse (response: HttpListenerResponse) (events: (string * string) list) =
    response.StatusCode <- 200
    response.ContentType <- "text/event-stream"
    response.ContentEncoding <- Encoding.UTF8
    response.SendChunked <- true
    use writer = new StreamWriter(response.OutputStream, Encoding.UTF8)
    for (eventName, payload) in events do
        if eventName <> "" then
            writer.Write("event: ")
            writer.WriteLine(eventName)
        writer.Write("data: ")
        writer.WriteLine(payload)
        writer.WriteLine()
        writer.Flush()
    response.OutputStream.Close()

let largeText seed count =
    String.replicate count seed

let openAiCompleteResponse (scenario: string) (requestIndex: int) (requestBody: string) =
    let model =
        try
            let doc = JsonDocument.Parse(requestBody)
            tryGetString "model" doc.RootElement |> Option.defaultValue "gpt-5.4"
        with _ ->
            "gpt-5.4"

    let text =
        match scenario, requestIndex with
        | ("truncate" | "summary_high" | "edge_override"), 1 -> largeText "PLAN " 1400
        | ("truncate" | "summary_high" | "edge_override"), _ -> "Implementation complete."
        | _ when requestIndex = 1 -> "Mock response."
        | _ -> "Done."

    box
        {| id = $"resp_{requestIndex}"
           model = model
           status = "completed"
           output = [| box {| ``type`` = "message"; content = [| box {| ``type`` = "output_text"; text = text |} |] |} |]
           usage = {| input_tokens = 10; output_tokens = 20 |} |}

let anthropicCompleteResponse (requestIndex: int) (requestBody: string) =
    let model =
        try
            let doc = JsonDocument.Parse(requestBody)
            tryGetString "model" doc.RootElement |> Option.defaultValue "claude-sonnet-4-6"
        with _ ->
            "claude-sonnet-4-6"

    let text =
        if requestIndex = 3 then
            "Downstream summary complete."
        else
            "Anthropic complete response."

    box
        {| id = $"msg_{requestIndex}"
           model = model
           content = [| box {| ``type`` = "text"; text = text |} |]
           stop_reason = "end_turn"
           usage = {| input_tokens = 12; output_tokens = 18 |} |}

let anthropicStreamEvents requestIndex =
    match requestIndex with
    | 1 ->
        [ "message_start", """{"message":{"id":"msg_1","model":"claude-sonnet-4-6","usage":{"input_tokens":10}}}"""
          "content_block_start", """{"index":0,"content_block":{"type":"thinking"}}"""
          "content_block_delta", """{"index":0,"delta":{"type":"thinking_delta","thinking":"reason-1 "}}"""
          "content_block_delta", """{"index":0,"delta":{"type":"thinking_delta","thinking":"reason-2"}}"""
          "content_block_stop", """{"index":0}"""
          "content_block_start", """{"index":1,"content_block":{"type":"tool_use","id":"call_1","name":"write_file","input":{}}}"""
          "content_block_delta", """{"index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\":\"notes.txt\""}}"""
          "content_block_delta", """{"index":1,"delta":{"type":"input_json_delta","partial_json":",\"content\":\"hello\"}"}}"""
          "content_block_stop", """{"index":1}"""
          "message_delta", """{"delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":8}}"""
          "message_stop", "{}" ]
    | 2 ->
        [ "message_start", """{"message":{"id":"msg_2","model":"claude-sonnet-4-6","usage":{"input_tokens":14}}}"""
          "content_block_start", """{"index":0,"content_block":{"type":"text"}}"""
          "content_block_delta", """{"index":0,"delta":{"type":"text_delta","text":"Agent completed."}}"""
          "content_block_stop", """{"index":0}"""
          "message_delta", """{"delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":12}}"""
          "message_stop", "{}" ]
    | _ ->
        [ "message_start", """{"message":{"id":"msg_3","model":"claude-sonnet-4-6","usage":{"input_tokens":8}}}"""
          "content_block_start", """{"index":0,"content_block":{"type":"text"}}"""
          "content_block_delta", """{"index":0,"delta":{"type":"text_delta","text":"Fallback."}}"""
          "content_block_stop", """{"index":0}"""
          "message_delta", """{"delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6}}"""
          "message_stop", "{}" ]

let openAiStreamEvents requestIndex =
    [ "response.created", $"{{\"id\":\"resp_{requestIndex}\",\"model\":\"gpt-5.4\"}}"
      "response.output_text.delta", """{"delta":"streamed "}"""
      "response.output_text.delta", """{"delta":"response"}"""
      "response.completed", $"{{\"response\":{{\"id\":\"resp_{requestIndex}\",\"model\":\"gpt-5.4\",\"status\":\"completed\",\"usage\":{{\"input_tokens\":5,\"output_tokens\":2}}}}}}" ]

let listener = new HttpListener()
listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/")
listener.Start()
eprintfn "MockLlmServer listening on http://127.0.0.1:%d/ scenario=%s" config.Port config.Scenario

while true do
    let context = listener.GetContext()
    let requestIndex = nextRequestIndex ()
    let body = readBody context.Request
    let kind =
        if context.Request.Url.AbsolutePath.Contains("/v1/responses") then "openai"
        elif context.Request.Url.AbsolutePath.Contains("/v1/messages") then "anthropic"
        else "unknown"
    File.WriteAllText(Path.Combine(config.RecordDir, sprintf "%03d-%s.json" requestIndex kind), body)

    match context.Request.Url.AbsolutePath with
    | path when path.EndsWith("/v1/responses") ->
        if hasStreamFlag body then
            writeSse context.Response (openAiStreamEvents requestIndex)
        else
            writeJson context.Response 200 (openAiCompleteResponse config.Scenario requestIndex body)
    | path when path.EndsWith("/v1/messages") ->
        if hasStreamFlag body then
            writeSse context.Response (anthropicStreamEvents requestIndex)
        else
            writeJson context.Response 200 (anthropicCompleteResponse requestIndex body)
    | _ ->
        writeJson context.Response 404 (box {| error = "not found" |})
