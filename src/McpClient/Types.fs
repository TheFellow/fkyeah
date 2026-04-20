namespace McpClient

open System
open System.Text.Json
open System.Threading
open System.Collections.Generic

[<RequireQualifiedAccess>]
type McpError =
    | InvalidConfiguration of string
    | InvalidResponse of string
    | RpcError of code: int * message: string
    | NotConnected
    | TransportClosed of string
    | ProcessExited of int
    | Timeout of string

[<RequireQualifiedAccess>]
type McpTransportKind =
    | Stdio
    | HttpSse
    | StreamableHttp

    static member Parse(value: string) =
        match value.Trim().ToLowerInvariant() with
        | "stdio" -> Some McpTransportKind.Stdio
        | "sse"
        | "http+sse"
        | "http-sse" -> Some McpTransportKind.HttpSse
        | "streamablehttp"
        | "streamable-http"
        | "streamable_http" -> Some McpTransportKind.StreamableHttp
        | _ -> None

type McpTransport =
    { Connect: unit -> Async<Result<unit, McpError>>
      Send: byte array -> Async<Result<unit, McpError>>
      Receive: CancellationToken -> IAsyncEnumerable<byte array>
      Disconnect: unit -> Async<unit> }

type McpServerConfig =
    { Name: string
      Transport: McpTransportKind
      Command: string option
      Args: string list
      Env: Map<string, string>
      Url: string option
      RequestUrl: string option
      Headers: Map<string, string> }

type McpConnectionPolicy =
    { AutoReconnect: bool
      MaxRetries: int
      RetryDelay: TimeSpan
      RefreshToolsOnReconnect: bool }

    static member Default =
        { AutoReconnect = true
          MaxRetries = 2
          RetryDelay = TimeSpan.FromMilliseconds(250.0)
          RefreshToolsOnReconnect = true }

    static member NoRetry =
        { AutoReconnect = false
          MaxRetries = 0
          RetryDelay = TimeSpan.Zero
          RefreshToolsOnReconnect = false }

type McpToolDefinition =
    { Name: string
      Description: string
      InputSchema: JsonElement }

type McpToolCallResult = { Content: JsonElement; IsError: bool }

type McpRemoteServer =
    { Config: McpServerConfig
      ListTools: unit -> Async<Result<McpToolDefinition list, McpError>>
      CallTool: string -> JsonElement -> Async<Result<McpToolCallResult, McpError>>
      Cleanup: unit -> Async<unit> }

type DiscoveredMcpTool =
    { ServerName: string
      Definition: McpToolDefinition }

module McpError =

    let describe error =
        match error with
        | McpError.InvalidConfiguration message -> $"Invalid configuration: {message}"
        | McpError.InvalidResponse message -> $"Invalid response: {message}"
        | McpError.RpcError(code, message) -> $"RPC error {code}: {message}"
        | McpError.NotConnected -> "Not connected"
        | McpError.TransportClosed message -> $"Transport closed: {message}"
        | McpError.ProcessExited exitCode -> $"Process exited with code {exitCode}"
        | McpError.Timeout message -> $"Timeout: {message}"

module McpServerConfig =

    let validate (config: McpServerConfig) =
        let requireField (fieldName: string) (value: string option) =
            match value with
            | Some raw when not (String.IsNullOrWhiteSpace(raw)) -> Ok config
            | _ ->
                Error(
                    McpError.InvalidConfiguration
                        $"Server '{config.Name}' requires '{fieldName}' for {config.Transport}"
                )

        match config.Transport with
        | McpTransportKind.Stdio -> requireField "command" config.Command
        | McpTransportKind.HttpSse -> requireField "url" config.Url
        | McpTransportKind.StreamableHttp ->
            match config.Url, config.RequestUrl with
            | Some url, _
            | _, Some url when not (String.IsNullOrWhiteSpace(url)) -> Ok config
            | _ ->
                Error(
                    McpError.InvalidConfiguration
                        $"Server '{config.Name}' requires 'url' or 'requestUrl' for {config.Transport}"
                )
