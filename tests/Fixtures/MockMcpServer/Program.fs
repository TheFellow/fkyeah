module MockMcpServer

open System
open System.Text.Json

type Marker = class end

let private jsonElement (text: string) =
    JsonDocument.Parse(text).RootElement.Clone()

let private tryGetProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>

    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
        Some value
    else
        None

let private readArgumentsText (parameters: JsonElement) =
    match tryGetProperty "arguments" parameters with
    | Some arguments ->
        match tryGetProperty "text" arguments with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ ->
            match tryGetProperty "input" arguments with
            | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> ""
    | None -> ""

let private writeJsonLine (payload: obj) =
    let json = JsonSerializer.Serialize(payload)
    Console.Out.WriteLine(json)
    Console.Out.Flush()

let private writeResult id result =
    match id with
    | Some value ->
        writeJsonLine (
            box
                {| jsonrpc = "2.0"
                   id = value
                   result = result |}
        )
    | None -> ()

let private writeError id code message =
    match id with
    | Some value ->
        writeJsonLine (
            box
                {| jsonrpc = "2.0"
                   id = value
                   error = {| code = code; message = message |} |}
        )
    | None -> ()

[<EntryPoint>]
let main _ =
    let mutable keepRunning = true

    while keepRunning do
        let line = Console.ReadLine()

        if isNull line then
            keepRunning <- false
        elif String.IsNullOrWhiteSpace(line) then
            ()
        else
            try
                use document = JsonDocument.Parse(line)
                let root = document.RootElement

                let id =
                    tryGetProperty "id" root
                    |> Option.map (fun value ->
                        match value.ValueKind with
                        | JsonValueKind.String -> box (value.GetString())
                        | JsonValueKind.Number -> box (value.GetInt32())
                        | _ -> box (value.GetRawText()))

                let methodName =
                    tryGetProperty "method" root
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                    |> Option.map _.GetString()
                    |> Option.defaultValue ""

                let parameters =
                    tryGetProperty "params" root
                    |> Option.map _.Clone()
                    |> Option.defaultValue (JsonSerializer.SerializeToElement({| |}).Clone())

                match methodName with
                | "initialize" ->
                    writeResult
                        id
                        (jsonElement
                            """{"protocolVersion":"2025-03-26","capabilities":{},"serverInfo":{"name":"mock-mcp"}}""")
                | "tools/list" ->
                    writeResult
                        id
                        (jsonElement
                            """{"tools":[{"name":"echo_upper","description":"Uppercase input text","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}]}""")
                | "tools/call" ->
                    let toolName =
                        tryGetProperty "name" parameters
                        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                        |> Option.map _.GetString()
                        |> Option.defaultValue ""

                    if toolName = "echo_upper" then
                        let upper = (readArgumentsText parameters).ToUpperInvariant()

                        writeResult
                            id
                            (jsonElement
                                $"""{{"content":[{{"type":"text","text":{JsonSerializer.Serialize(upper)}}}],"isError":false}}""")
                    else
                        writeError id -32601 "Tool not found"
                | _ -> writeError id -32601 "Method not found"
            with ex ->
                Console.Error.WriteLine(ex.Message)
                Console.Error.Flush()

    0
