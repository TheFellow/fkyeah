namespace Attractor

open System
open System.IO
open System.Text
open System.Text.Json
open McpClient

module McpHandlers =

    let private resolveVisitCount (context: Context) =
        context.TryGet("node.visit_count")
        |> Option.bind (fun raw ->
            match Int32.TryParse(raw) with
            | true, value -> Some value
            | _ -> None)
        |> Option.defaultValue 1

    let private resolveStageDirs (logsRoot: string) (node: Node) (context: Context) =
        let rootDir = Path.Combine(logsRoot, node.Id)
        let visit = resolveVisitCount context
        Path.Combine(rootDir, sprintf "%03d" visit), rootDir

    let private ensureDir (path: string) =
        if not (Directory.Exists(path)) then
            Directory.CreateDirectory(path) |> ignore

    let private writeStageFile (stageDir: string) (rootDir: string) (fileName: string) (content: string) =
        ensureDir stageDir
        File.WriteAllText(Path.Combine(stageDir, fileName), content)

        if stageDir <> rootDir then
            ensureDir rootDir
            File.WriteAllText(Path.Combine(rootDir, fileName), content)

    let private tryParseJson (text: string) =
        try
            Some(JsonDocument.Parse(text).RootElement.Clone())
        with _ ->
            None

    let private serializeToElement<'T> (value: 'T) =
        JsonSerializer.SerializeToElement(value).Clone()

    let private serializeTools (tools: McpToolDefinition list) =
        tools
        |> List.map (fun tool ->
            {| name = tool.Name
               description = tool.Description
               inputSchema = tool.InputSchema |})
        |> JsonSerializer.Serialize

    let private deserializeTools (raw: string) =
        try
            use document = JsonDocument.Parse(raw)

            if document.RootElement.ValueKind <> JsonValueKind.Array then
                None
            else
                document.RootElement.EnumerateArray()
                |> Seq.map (fun item ->
                    let name = item.GetProperty("name").GetString()

                    let description =
                        let mutable descriptionElement = Unchecked.defaultof<JsonElement>

                        if
                            item.TryGetProperty("description", &descriptionElement)
                            && descriptionElement.ValueKind = JsonValueKind.String
                        then
                            descriptionElement.GetString()
                        else
                            ""

                    let schema =
                        let mutable schemaElement = Unchecked.defaultof<JsonElement>

                        if item.TryGetProperty("inputSchema", &schemaElement) then
                            schemaElement.Clone()
                        else
                            serializeToElement {| |}

                    { Name = name
                      Description = description
                      InputSchema = schema })
                |> Seq.toList
                |> Some
        with _ ->
            None

    let private configBaseDir (graph: Graph) =
        let cwd = graph.GetGraphAttrString("cwd", "")

        if cwd = "" then Environment.CurrentDirectory
        elif Path.IsPathRooted(cwd) then cwd
        else Path.GetFullPath(cwd)

    let private loadConfigs (node: Node) (graph: Graph) =
        let configFile = node.GetAttrString("mcp_config_file", "").Trim()
        let inlineConfigs = graph.GetGraphAttrString("mcp_servers", "").Trim()

        if configFile <> "" then
            let path =
                if Path.IsPathRooted(configFile) then
                    configFile
                else
                    Path.Combine(configBaseDir graph, configFile)

            Config.parseConfigFile path
        elif inlineConfigs <> "" then
            Config.parseConfigText inlineConfigs
        else
            Error(
                McpError.InvalidConfiguration
                    "Missing MCP configuration. Set node attr 'mcp_config_file' or graph attr 'mcp_servers'."
            )

    let private argumentsFromContext (context: Context) =
        let primaryValue =
            [ context.TryGet("tool.output")
              context.TryGet("last_response")
              context.TryGet("human.gate.input") ]
            |> List.choose id
            |> List.tryFind (fun value -> not (String.IsNullOrWhiteSpace(value)))

        match primaryValue with
        | Some value ->
            match tryParseJson value with
            | Some parsed -> parsed
            | None -> serializeToElement {| text = value |}
        | None ->
            context.Snapshot()
            |> JsonSerializer.SerializeToElement
            |> fun value -> value.Clone()

    let private extractToolOutput (result: McpToolCallResult) =
        let content = result.Content

        if content.ValueKind = JsonValueKind.Array then
            content.EnumerateArray()
            |> Seq.choose (fun item ->
                let mutable textElement = Unchecked.defaultof<JsonElement>

                if
                    item.ValueKind = JsonValueKind.Object
                    && item.TryGetProperty("text", &textElement)
                    && textElement.ValueKind = JsonValueKind.String
                then
                    Some(textElement.GetString())
                else
                    None)
            |> String.concat "\n"
            |> fun text -> if text <> "" then text else content.GetRawText()
        else
            content.GetRawText()

    let private cacheKey serverName = $"mcp.tools.{serverName}"

    type McpToolHandler(?serverFactory: McpServerConfig -> McpConnectionPolicy -> Result<McpRemoteServer, McpError>) =

        let serverFactory = defaultArg serverFactory Server.createServer

        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let stageDir, rootDir = resolveStageDirs logsRoot node context
                let requestTrail = ResizeArray<obj>()

                let fail reason =
                    let outcome = Outcome.Fail(reason)
                    HandlerArtifacts.writeStatus stageDir rootDir outcome
                    outcome

                let serverName = node.GetAttrString("mcp_server", "").Trim()
                let toolName = node.GetAttrString("mcp_tool", "").Trim()

                if serverName = "" then
                    fail "Missing required node attribute 'mcp_server'"
                elif toolName = "" then
                    fail "Missing required node attribute 'mcp_tool'"
                else
                    match loadConfigs node graph with
                    | Error error -> fail (McpError.describe error)
                    | Ok configs ->
                        match configs |> List.tryFind (fun config -> config.Name = serverName) with
                        | None ->
                            let available =
                                configs |> List.map (fun config -> config.Name) |> String.concat ", "

                            fail $"MCP server '{serverName}' not found. Available servers: {available}"
                        | Some config ->
                            match serverFactory config McpConnectionPolicy.Default with
                            | Error error -> fail (McpError.describe error)
                            | Ok server ->
                                try
                                    let toolsResult =
                                        match context.TryGet(cacheKey serverName) |> Option.bind deserializeTools with
                                        | Some cached -> Ok cached
                                        | None ->
                                            requestTrail.Add(box {| method = "initialize" |})
                                            requestTrail.Add(box {| method = "tools/list" |})
                                            let listed = server.ListTools() |> Async.RunSynchronously

                                            match listed with
                                            | Ok tools -> Ok tools
                                            | Error error -> Error error

                                    match toolsResult with
                                    | Error error -> fail (McpError.describe error)
                                    | Ok tools ->
                                        match tools |> List.tryFind (fun tool -> tool.Name = toolName) with
                                        | None ->
                                            let available =
                                                tools
                                                |> List.map (fun tool -> tool.Name)
                                                |> fun names ->
                                                    if List.isEmpty names then
                                                        "(none)"
                                                    else
                                                        String.concat ", " names

                                            let requestJson =
                                                JsonSerializer.Serialize(
                                                    {| server = serverName
                                                       tool = toolName
                                                       requests = requestTrail |> Seq.toList |},
                                                    JsonSerializerOptions(WriteIndented = true)
                                                )

                                            writeStageFile stageDir rootDir "mcp_request.json" requestJson

                                            fail
                                                $"MCP tool '{toolName}' not found on server '{serverName}'. Available tools: {available}"
                                        | Some _ ->
                                            let arguments = argumentsFromContext context

                                            requestTrail.Add(
                                                box
                                                    {| method = "tools/call"
                                                       name = toolName
                                                       arguments = arguments |}
                                            )

                                            let requestJson =
                                                JsonSerializer.Serialize(
                                                    {| server = serverName
                                                       tool = toolName
                                                       requests = requestTrail |> Seq.toList |},
                                                    JsonSerializerOptions(WriteIndented = true)
                                                )

                                            writeStageFile stageDir rootDir "mcp_request.json" requestJson

                                            match server.CallTool toolName arguments |> Async.RunSynchronously with
                                            | Error error ->
                                                let responseJson =
                                                    JsonSerializer.Serialize(
                                                        {| error = McpError.describe error |},
                                                        JsonSerializerOptions(WriteIndented = true)
                                                    )

                                                writeStageFile stageDir rootDir "mcp_response.json" responseJson
                                                fail (McpError.describe error)
                                            | Ok result ->
                                                let responseJson =
                                                    JsonSerializer.Serialize(
                                                        {| isError = result.IsError
                                                           content = result.Content |},
                                                        JsonSerializerOptions(WriteIndented = true)
                                                    )

                                                writeStageFile stageDir rootDir "mcp_response.json" responseJson

                                                if result.IsError then
                                                    fail (extractToolOutput result)
                                                else
                                                    let updates =
                                                        Map.ofList
                                                            [ "tool.output", extractToolOutput result
                                                              cacheKey serverName, serializeTools tools ]

                                                    let outcome =
                                                        Outcome.Success(
                                                            notes = $"MCP tool completed: {serverName}/{toolName}",
                                                            contextUpdates = updates
                                                        )

                                                    HandlerArtifacts.writeStatus stageDir rootDir outcome
                                                    outcome
                                finally
                                    server.Cleanup() |> Async.RunSynchronously
