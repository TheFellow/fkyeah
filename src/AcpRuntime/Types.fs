namespace AcpRuntime

open System
open System.Text.Json

[<RequireQualifiedAccess>]
type AcpTransportKind =
    | Stdio
    | WebSocket
    | HttpSse
    | InMemory

    override this.ToString() =
        match this with
        | AcpTransportKind.Stdio -> "stdio"
        | AcpTransportKind.WebSocket -> "websocket"
        | AcpTransportKind.HttpSse -> "http_sse"
        | AcpTransportKind.InMemory -> "memory"

    static member Parse(value: string) =
        match value.Trim().ToLowerInvariant() with
        | "stdio" -> Some AcpTransportKind.Stdio
        | "websocket"
        | "web_socket"
        | "web-socket"
        | "ws" -> Some AcpTransportKind.WebSocket
        | "http+sse"
        | "http_sse"
        | "http-sse"
        | "sse" -> Some AcpTransportKind.HttpSse
        | "memory"
        | "inmemory"
        | "in_memory"
        | "in-memory" -> Some AcpTransportKind.InMemory
        | _ -> None

[<RequireQualifiedAccess>]
type PermissionStrategy =
    | DenyAll
    | AutoApprove
    | ConsolePrompt

[<RequireQualifiedAccess>]
type AcpError =
    | AlreadyConnected
    | NotConnected
    | ConnectionClosed
    | InvalidPayload of string
    | InvalidResponse of string
    | MissingResult of string
    | TimedOut of string
    | PermissionDenied of string
    | PathOutsideRoot of string
    | TransportClosed
    | ProcessExited of int
    | UnknownDelegateMethod of string

[<CLIMutable>]
type AcpEndpoint =
    { Transport: AcpTransportKind
      Command: string option
      Args: string list
      Url: string option
      Headers: Map<string, string>
      WorkingDirectory: string option }

[<CLIMutable>]
type ServerInfo =
    { Name: string
      Version: string option }

type ContentBlock =
    | Text of string
    | Image of uri: string

[<CLIMutable>]
type InitializeResult =
    { ProtocolVersion: string
      Capabilities: JsonElement
      ServerInfo: ServerInfo option }

[<CLIMutable>]
type PromptResult =
    { SessionId: string
      Content: ContentBlock list
      StopReason: string option
      Metadata: JsonElement option }

[<CLIMutable>]
type PromptMetadata =
    { McpServersJson: string option }

type NotificationObserver = string -> JsonElement option -> unit

module AcpError =

    let describe error =
        match error with
        | AcpError.AlreadyConnected -> "Already connected"
        | AcpError.NotConnected -> "Not connected"
        | AcpError.ConnectionClosed -> "Connection closed"
        | AcpError.InvalidPayload message -> $"Invalid payload: {message}"
        | AcpError.InvalidResponse message -> $"Invalid response: {message}"
        | AcpError.MissingResult message -> $"Missing result: {message}"
        | AcpError.TimedOut message -> $"Timed out: {message}"
        | AcpError.PermissionDenied message -> $"Permission denied: {message}"
        | AcpError.PathOutsideRoot message -> $"Path outside root: {message}"
        | AcpError.TransportClosed -> "Transport closed"
        | AcpError.ProcessExited exitCode -> $"Process exited with code {exitCode}"
        | AcpError.UnknownDelegateMethod methodName -> $"Unknown delegate method: {methodName}"

module Json =

    let serializeToElement<'T> (value: 'T) =
        JsonSerializer.SerializeToElement(value).Clone()

    let tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some(value.Clone())
        else
            None

    let tryGetString (name: string) (element: JsonElement) =
        tryGetProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                Some(value.GetString())
            else
                None)

    let deserialize<'T> (element: JsonElement) =
        try
            Ok(JsonSerializer.Deserialize<'T>(element.GetRawText()))
        with ex ->
            Error ex.Message

module ContentBlock =

    let text value = Text value

    let private encodeOne block =
        match block with
        | Text value -> Json.serializeToElement {| ``type`` = "text"; text = value |}
        | Image uri -> Json.serializeToElement {| ``type`` = "image"; uri = uri |}

    let toElement blocks =
        blocks |> List.map encodeOne |> Json.serializeToElement

    let private decodeOne (element: JsonElement) =
        match Json.tryGetString "type" element with
        | Some "text" ->
            match Json.tryGetString "text" element with
            | Some value -> Ok(Text value)
            | None -> Error "Content block is missing text"
        | Some "image" ->
            match Json.tryGetString "uri" element with
            | Some value -> Ok(Image value)
            | None -> Error "Content block is missing uri"
        | Some other -> Error $"Unsupported content block type '{other}'"
        | None -> Error "Content block is missing type"

    let ofElement (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            Error "Content blocks must be an array"
        else
            element.EnumerateArray()
            |> Seq.fold
                (fun state item ->
                    match state, decodeOne item with
                    | Ok acc, Ok block -> Ok(block :: acc)
                    | Error error, _
                    | _, Error error -> Error error)
                (Ok [])
            |> Result.map List.rev
