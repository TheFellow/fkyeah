module MockAcpAgent

open System
open System.Text.Json
open JsonRpc

type Marker = class end

let private tryGetProperty (name: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>

    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
        Some(value.Clone())
    else
        None

let private parseId (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.String -> StringId(element.GetString())
    | JsonValueKind.Number -> NumberId(element.GetInt32())
    | _ -> StringId(element.GetRawText())

let private serializeToElement<'T> (value: 'T) =
    JsonSerializer.SerializeToElement(value).Clone()

let private writePayload (payload: byte array) =
    Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(payload))
    Console.Out.Flush()

let private writeResult id result =
    writePayload (Codec.encodeResponse id result)

let private writeError id code message =
    writePayload (
        Codec.encodeError
            id
            { Code = code
              Message = message
              Data = None }
    )

let private writeRequest id methodName parameters =
    writePayload (
        Codec.encode
            { Id = id
              Method = methodName
              Params = Some parameters }
    )

let private promptText (parameters: JsonElement) =
    match tryGetProperty "prompt" parameters with
    | Some blocks when blocks.ValueKind = JsonValueKind.Array ->
        blocks.EnumerateArray()
        |> Seq.choose (fun item ->
            match tryGetProperty "text" item with
            | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
            | _ -> None)
        |> String.concat "\n"
    | _ -> ""

let private promptMetadata (parameters: JsonElement) = tryGetProperty "metadata" parameters

let private mcpServersJson (parameters: JsonElement) =
    promptMetadata parameters
    |> Option.bind (tryGetProperty "mcpServersJson")
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
    |> Option.map _.GetString()

[<EntryPoint>]
let main argv =
    let slow = argv |> Array.exists ((=) "--slow")
    let denyTest = argv |> Array.exists ((=) "--deny-test")
    let mutable delegateRequestId = 1000
    let mutable keepRunning = true

    while keepRunning do
        let line = Console.ReadLine()

        if isNull line then
            keepRunning <- false
        elif String.IsNullOrWhiteSpace(line) then
            ()
        else
            match Codec.decode (System.Text.Encoding.UTF8.GetBytes(line)) with
            | Error error ->
                Console.Error.WriteLine(error)
                Console.Error.Flush()
            | Ok(Notification _) -> ()
            | Ok(Response _) -> ()
            | Ok(Request request) ->
                match request.Method with
                | "initialize" ->
                    let payload =
                        serializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| prompt = true; delegates = true |}
                               serverInfo =
                                {| name = "mock-acp-agent"
                                   version = "1" |} |}

                    writeResult request.Id payload
                | "session/prompt" ->
                    let parameters = request.Params |> Option.defaultValue (serializeToElement {| |})

                    let sessionId =
                        tryGetProperty "sessionId" parameters
                        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
                        |> Option.map _.GetString()
                        |> Option.defaultValue "mock-session"

                    let mcpServers = mcpServersJson parameters

                    let mcpNote =
                        match mcpServers with
                        | Some _ -> " [mcp servers provided]"
                        | None -> ""

                    if slow then
                        Threading.Thread.Sleep(2000)

                    let denialNote =
                        if denyTest then
                            delegateRequestId <- delegateRequestId + 1
                            let delegateId = NumberId delegateRequestId

                            writeRequest
                                delegateId
                                "filesystem/read_text_file"
                                (serializeToElement {| path = "../secrets.txt" |})

                            let delegateResponseLine = Console.ReadLine()

                            if isNull delegateResponseLine then
                                "delegate request had no response"
                            else
                                match Codec.decode (System.Text.Encoding.UTF8.GetBytes(delegateResponseLine)) with
                                | Ok(Response(_, Error error)) -> $" denied: {error.Message}"
                                | Ok(Response(_, Ok _)) -> " delegate request unexpectedly succeeded"
                                | Ok _ -> " delegate request returned unexpected payload"
                                | Error error -> $" delegate response malformed: {error}"
                        else
                            ""

                    let responseText =
                        $"Mock ACP agent handled: {promptText parameters}{denialNote}{mcpNote}"

                    let payload =
                        serializeToElement
                            {| sessionId = sessionId
                               content =
                                [ {| ``type`` = "text"
                                     text = responseText |} ]
                               stopReason = "completed"
                               metadata =
                                {| sawMcpServers = mcpServers.IsSome
                                   mcpServersJson = mcpServers |} |}

                    writeResult request.Id payload
                | "session/cancel" -> writeResult request.Id (serializeToElement {| cancelled = true |})
                | _ -> writeError request.Id -32601 "Method not found"

    0
