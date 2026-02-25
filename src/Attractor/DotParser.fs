namespace Attractor

open System
open System.Text
open System.Text.RegularExpressions

/// Token types for the DOT lexer
[<RequireQualifiedAccess>]
type Token =
    | Digraph
    | Graph
    | Node
    | Edge
    | Subgraph
    | LBrace
    | RBrace
    | LBracket
    | RBracket
    | Semicolon
    | Comma
    | Equals
    | Arrow       // ->
    | Identifier of string
    | QuotedString of string
    | IntegerLit of int
    | FloatLit of float
    | BoolLit of bool
    | DurationLit of Duration
    | Eof

module Lexer =
    let private isIdentStart c = Char.IsLetter(c) || c = '_'
    let private isIdentChar c = Char.IsLetterOrDigit(c) || c = '_' || c = '.'

    let private stripComments (input: string) =
        let sb = StringBuilder(input.Length)
        let mutable i = 0
        let mutable inString = false
        while i < input.Length do
            if inString then
                // Inside a quoted string — pass through everything, handle escapes
                sb.Append(input[i]) |> ignore
                if input[i] = '\\' && i + 1 < input.Length then
                    // Escaped character — pass both through
                    sb.Append(input[i + 1]) |> ignore
                    i <- i + 2
                elif input[i] = '"' then
                    // End of string
                    inString <- false
                    i <- i + 1
                else
                    i <- i + 1
            elif input[i] = '"' then
                // Start of string — pass through without comment detection
                inString <- true
                sb.Append(input[i]) |> ignore
                i <- i + 1
            elif i + 1 < input.Length && input[i] = '/' && input[i + 1] = '/' then
                // Line comment — skip to end of line
                while i < input.Length && input[i] <> '\n' do
                    i <- i + 1
            elif i + 1 < input.Length && input[i] = '/' && input[i + 1] = '*' then
                // Block comment — skip to */
                i <- i + 2
                while i + 1 < input.Length && not (input[i] = '*' && input[i + 1] = '/') do
                    i <- i + 1
                if i + 1 < input.Length then
                    i <- i + 2
            else
                sb.Append(input[i]) |> ignore
                i <- i + 1
        sb.ToString()

    let private skipWhitespace (input: string) (pos: int) =
        let mutable p = pos
        while p < input.Length && Char.IsWhiteSpace(input[p]) do
            p <- p + 1
        p

    let private readQuotedString (input: string) (startPos: int) =
        // startPos points to the opening quote
        let sb = StringBuilder()
        let mutable p = startPos + 1
        while p < input.Length && input[p] <> '"' do
            if input[p] = '\\' && p + 1 < input.Length then
                match input[p + 1] with
                | 'n' -> sb.Append('\n') |> ignore; p <- p + 2
                | 't' -> sb.Append('\t') |> ignore; p <- p + 2
                | '\\' -> sb.Append('\\') |> ignore; p <- p + 2
                | '"' -> sb.Append('"') |> ignore; p <- p + 2
                | c -> sb.Append('\\').Append(c) |> ignore; p <- p + 2
            else
                sb.Append(input[p]) |> ignore
                p <- p + 1
        if p < input.Length then
            p <- p + 1 // skip closing quote
        (sb.ToString(), p)

    let private readIdentifier (input: string) (startPos: int) =
        let mutable p = startPos
        while p < input.Length && isIdentChar input[p] do
            p <- p + 1
        (input.Substring(startPos, p - startPos), p)

    let private readNumber (input: string) (startPos: int) =
        let mutable p = startPos
        if p < input.Length && input[p] = '-' then
            p <- p + 1
        while p < input.Length && Char.IsDigit(input[p]) do
            p <- p + 1
        // Check for float
        if p < input.Length && input[p] = '.' && p + 1 < input.Length && Char.IsDigit(input[p + 1]) then
            p <- p + 1
            while p < input.Length && Char.IsDigit(input[p]) do
                p <- p + 1
            let s = input.Substring(startPos, p - startPos)
            (Token.FloatLit(Double.Parse(s)), p)
        else
            let s = input.Substring(startPos, p - startPos)
            let intVal = Int32.Parse(s)
            // Check for duration suffix
            if p < input.Length then
                if p + 1 < input.Length && input[p] = 'm' && input[p + 1] = 's' then
                    (Token.DurationLit(Duration.FromMs(int64 intVal)), p + 2)
                elif input[p] = 's' then
                    (Token.DurationLit(Duration.FromSeconds(int64 intVal)), p + 1)
                elif input[p] = 'm' && (p + 1 >= input.Length || not (isIdentChar input[p + 1])) then
                    (Token.DurationLit(Duration.FromMinutes(int64 intVal)), p + 1)
                elif input[p] = 'h' then
                    (Token.DurationLit(Duration.FromHours(int64 intVal)), p + 1)
                elif input[p] = 'd' && (p + 1 >= input.Length || not (isIdentChar input[p + 1])) then
                    (Token.DurationLit(Duration.FromDays(int64 intVal)), p + 1)
                else
                    (Token.IntegerLit intVal, p)
            else
                (Token.IntegerLit intVal, p)

    let tokenize (input: string) : Token list =
        let input = stripComments input
        let tokens = ResizeArray<Token>()
        let mutable pos = 0

        while pos < input.Length do
            pos <- skipWhitespace input pos
            if pos >= input.Length then
                ()
            else
                let c = input[pos]
                match c with
                | '{' -> tokens.Add(Token.LBrace); pos <- pos + 1
                | '}' -> tokens.Add(Token.RBrace); pos <- pos + 1
                | '[' -> tokens.Add(Token.LBracket); pos <- pos + 1
                | ']' -> tokens.Add(Token.RBracket); pos <- pos + 1
                | ';' -> tokens.Add(Token.Semicolon); pos <- pos + 1
                | ',' -> tokens.Add(Token.Comma); pos <- pos + 1
                | '=' -> tokens.Add(Token.Equals); pos <- pos + 1
                | '-' when pos + 1 < input.Length && input[pos + 1] = '>' ->
                    tokens.Add(Token.Arrow); pos <- pos + 2
                | '-' when pos + 1 < input.Length && (Char.IsDigit(input[pos + 1]) || input[pos + 1] = '.') ->
                    let (tok, newPos) = readNumber input pos
                    tokens.Add(tok); pos <- newPos
                | '"' ->
                    let (s, newPos) = readQuotedString input pos
                    tokens.Add(Token.QuotedString s); pos <- newPos
                | c when Char.IsDigit(c) ->
                    let (tok, newPos) = readNumber input pos
                    tokens.Add(tok); pos <- newPos
                | c when isIdentStart c ->
                    let (id, newPos) = readIdentifier input pos
                    match id with
                    | "digraph" -> tokens.Add(Token.Digraph)
                    | "graph" -> tokens.Add(Token.Graph)
                    | "node" -> tokens.Add(Token.Node)
                    | "edge" -> tokens.Add(Token.Edge)
                    | "subgraph" -> tokens.Add(Token.Subgraph)
                    | "true" -> tokens.Add(Token.BoolLit true)
                    | "false" -> tokens.Add(Token.BoolLit false)
                    | _ -> tokens.Add(Token.Identifier id)
                    pos <- newPos
                | _ ->
                    pos <- pos + 1 // skip unknown characters

        tokens.Add(Token.Eof)
        tokens |> Seq.toList

/// Parser for the DOT subset
module DotParser =

    type private ParseState =
        { Tokens: Token array
          mutable Pos: int }

        member this.Current =
            if this.Pos < this.Tokens.Length then
                this.Tokens[this.Pos]
            else
                Token.Eof

        member this.Peek(offset: int) =
            let idx = this.Pos + offset
            if idx < this.Tokens.Length then
                this.Tokens[idx]
            else
                Token.Eof

        member this.Advance() =
            if this.Pos < this.Tokens.Length then
                this.Pos <- this.Pos + 1

        member this.Expect(expected: Token) =
            if this.Current = expected then
                this.Advance()
            else
                failwithf "Expected %A but got %A at position %d" expected this.Current this.Pos

        member this.TryConsume(tok: Token) =
            if this.Current = tok then
                this.Advance()
                true
            else
                false

    let private parseValue (state: ParseState) : AttrValue =
        match state.Current with
        | Token.QuotedString s ->
            state.Advance()
            AttrValue.String s
        | Token.IntegerLit i ->
            state.Advance()
            AttrValue.Integer i
        | Token.FloatLit f ->
            state.Advance()
            AttrValue.Float f
        | Token.BoolLit b ->
            state.Advance()
            AttrValue.Boolean b
        | Token.DurationLit d ->
            state.Advance()
            AttrValue.Duration d
        | Token.Identifier id ->
            state.Advance()
            AttrValue.String id
        | other ->
            failwithf "Expected a value but got %A at position %d" other state.Pos

    let private parseAttrKey (state: ParseState) : string =
        match state.Current with
        | Token.Identifier id ->
            state.Advance()
            id
        | Token.QuotedString s ->
            state.Advance()
            s
        | other ->
            failwithf "Expected attribute key but got %A at position %d" other state.Pos

    let private parseAttrBlock (state: ParseState) : Map<string, AttrValue> =
        if state.Current <> Token.LBracket then
            Map.empty
        else
            state.Advance() // consume [
            let attrs = ResizeArray<string * AttrValue>()
            while state.Current <> Token.RBracket && state.Current <> Token.Eof do
                // Parse key = value
                let key = parseAttrKey state
                state.Expect(Token.Equals)
                let value = parseValue state
                attrs.Add(key, value)
                // Consume optional comma or semicolon
                state.TryConsume(Token.Comma) |> ignore
                state.TryConsume(Token.Semicolon) |> ignore
            state.TryConsume(Token.RBracket) |> ignore
            attrs |> Seq.fold (fun m (k, v) -> Map.add k v m) Map.empty

    let private mergeAttrs (defaults: Map<string, AttrValue>) (explicit: Map<string, AttrValue>) =
        Map.fold (fun acc k v ->
            if Map.containsKey k acc then acc else Map.add k v acc
        ) explicit defaults

    type private ParsedStatement =
        | NodeDef of id: string * attrs: Map<string, AttrValue>
        | EdgeDef of nodes: string list * attrs: Map<string, AttrValue>
        | GraphAttrBlock of attrs: Map<string, AttrValue>
        | NodeDefaultBlock of attrs: Map<string, AttrValue>
        | EdgeDefaultBlock of attrs: Map<string, AttrValue>
        | GraphAttrDecl of key: string * value: AttrValue
        | SubgraphBlock of name: string option * statements: ParsedStatement list

    let rec private parseStatement (state: ParseState) : ParsedStatement option =
        state.TryConsume(Token.Semicolon) |> ignore

        match state.Current with
        | Token.Eof | Token.RBrace -> None

        | Token.Graph ->
            state.Advance()
            if state.Current = Token.LBracket then
                let attrs = parseAttrBlock state
                state.TryConsume(Token.Semicolon) |> ignore
                Some(GraphAttrBlock attrs)
            else
                // graph key = value
                let key = parseAttrKey state
                state.Expect(Token.Equals)
                let value = parseValue state
                state.TryConsume(Token.Semicolon) |> ignore
                Some(GraphAttrDecl(key, value))

        | Token.Node ->
            state.Advance()
            let attrs = parseAttrBlock state
            state.TryConsume(Token.Semicolon) |> ignore
            Some(NodeDefaultBlock attrs)

        | Token.Edge ->
            state.Advance()
            let attrs = parseAttrBlock state
            state.TryConsume(Token.Semicolon) |> ignore
            Some(EdgeDefaultBlock attrs)

        | Token.Subgraph ->
            state.Advance()
            let name =
                match state.Current with
                | Token.Identifier id ->
                    state.Advance()
                    Some id
                | _ -> None
            state.Expect(Token.LBrace)
            let stmts = parseStatements state
            state.Expect(Token.RBrace)
            state.TryConsume(Token.Semicolon) |> ignore
            Some(SubgraphBlock(name, stmts))

        | Token.Identifier _ ->
            // Could be: node statement, edge statement, or top-level attribute
            let id = parseAttrKey state

            match state.Current with
            | Token.Arrow ->
                // Edge statement: A -> B -> C [attrs]
                let nodeIds = ResizeArray<string>()
                nodeIds.Add(id)
                while state.TryConsume(Token.Arrow) do
                    let nextId = parseAttrKey state
                    nodeIds.Add(nextId)
                let attrs = parseAttrBlock state
                state.TryConsume(Token.Semicolon) |> ignore
                Some(EdgeDef(nodeIds |> Seq.toList, attrs))

            | Token.LBracket ->
                // Node statement with attributes
                let attrs = parseAttrBlock state
                state.TryConsume(Token.Semicolon) |> ignore
                Some(NodeDef(id, attrs))

            | Token.Equals ->
                // Top-level attribute: key = value
                state.Advance()
                let value = parseValue state
                state.TryConsume(Token.Semicolon) |> ignore
                Some(GraphAttrDecl(id, value))

            | _ ->
                // Bare node statement
                state.TryConsume(Token.Semicolon) |> ignore
                Some(NodeDef(id, Map.empty))

        | _ ->
            state.Advance()
            None

    and private parseStatements (state: ParseState) : ParsedStatement list =
        let stmts = ResizeArray<ParsedStatement>()
        let mutable cont = true
        while cont do
            match parseStatement state with
            | Some stmt -> stmts.Add(stmt)
            | None -> cont <- false
        stmts |> Seq.toList

    let private deriveSubgraphClass (name: string option) (stmts: ParsedStatement list) =
        // Try to get class from subgraph label or name
        let label =
            stmts
            |> List.tryPick (fun s ->
                match s with
                | GraphAttrBlock attrs -> attrs |> Map.tryFind "label"
                | GraphAttrDecl("label", v) -> Some v
                | _ -> None)
            |> Option.map (fun v -> v.AsString())

        match label with
        | Some l ->
            // Derive class: lowercase, replace spaces with hyphens, strip non-alphanum except hyphens
            l.ToLowerInvariant()
            |> fun s -> Regex.Replace(s, @"\s+", "-")
            |> fun s -> Regex.Replace(s, @"[^a-z0-9-]", "")
            |> Some
        | None ->
            name |> Option.map (fun n ->
                n.ToLowerInvariant()
                |> fun s -> Regex.Replace(s, @"[^a-z0-9-]", ""))

    let private buildGraph (name: string) (statements: ParsedStatement list) : Graph =
        let graphAttrs = ResizeArray<string * AttrValue>()
        let nodes = ResizeArray<string * Map<string, AttrValue>>()
        let edges = ResizeArray<string * string * Map<string, AttrValue>>()
        let mutable nodeDefaults = Map.empty<string, AttrValue>
        let mutable edgeDefaults = Map.empty<string, AttrValue>

        let rec processStatements (stmts: ParsedStatement list) (extraNodeAttrs: Map<string, AttrValue>) =
            for stmt in stmts do
                match stmt with
                | GraphAttrBlock attrs ->
                    for kv in attrs do
                        graphAttrs.Add(kv.Key, kv.Value)

                | GraphAttrDecl(key, value) ->
                    graphAttrs.Add(key, value)

                | NodeDefaultBlock attrs ->
                    nodeDefaults <- Map.fold (fun acc k v -> Map.add k v acc) nodeDefaults attrs

                | EdgeDefaultBlock attrs ->
                    edgeDefaults <- Map.fold (fun acc k v -> Map.add k v acc) edgeDefaults attrs

                | NodeDef(id, attrs) ->
                    let mergedAttrs = mergeAttrs nodeDefaults (mergeAttrs extraNodeAttrs attrs)
                    nodes.Add(id, mergedAttrs)

                | EdgeDef(nodeIds, attrs) ->
                    // Ensure nodes exist
                    for nid in nodeIds do
                        if not (nodes |> Seq.exists (fun (id, _) -> id = nid)) then
                            let mergedAttrs = mergeAttrs nodeDefaults extraNodeAttrs
                            nodes.Add(nid, mergedAttrs)
                    // Create edges for each pair
                    let mergedEdgeAttrs = mergeAttrs edgeDefaults attrs
                    for i in 0 .. nodeIds.Length - 2 do
                        edges.Add(nodeIds[i], nodeIds[i + 1], mergedEdgeAttrs)

                | SubgraphBlock(subName, subStmts) ->
                    let derivedClass = deriveSubgraphClass subName subStmts
                    // Get node defaults from within the subgraph
                    let subNodeDefaults =
                        subStmts
                        |> List.choose (fun s ->
                            match s with
                            | NodeDefaultBlock attrs -> Some attrs
                            | _ -> None)
                        |> List.fold (fun acc attrs -> Map.fold (fun a k v -> Map.add k v a) acc attrs) Map.empty

                    let subExtraAttrs =
                        let withDefaults = mergeAttrs nodeDefaults (mergeAttrs extraNodeAttrs subNodeDefaults)
                        match derivedClass with
                        | Some cls ->
                            // Add derived class to nodes
                            let existingClass =
                                withDefaults
                                |> Map.tryFind "class"
                                |> Option.map (fun v -> v.AsString())
                                |> Option.defaultValue ""
                            let newClass =
                                if existingClass = "" then cls
                                else existingClass + "," + cls
                            Map.add "class" (AttrValue.String newClass) withDefaults
                        | None -> withDefaults

                    let nonDefaultStmts =
                        subStmts
                        |> List.filter (fun s ->
                            match s with
                            | NodeDefaultBlock _ | EdgeDefaultBlock _ -> false
                            | GraphAttrBlock _ | GraphAttrDecl _ -> false
                            | _ -> true)
                    processStatements nonDefaultStmts subExtraAttrs

        processStatements statements Map.empty

        let nodeMap =
            nodes
            |> Seq.groupBy fst
            |> Seq.map (fun (id, defs) ->
                let mergedAttrs =
                    defs
                    |> Seq.map snd
                    |> Seq.fold (fun acc attrs -> Map.fold (fun a k v -> Map.add k v a) acc attrs) Map.empty
                id, { Id = id; Attributes = mergedAttrs })
            |> Map.ofSeq

        let edgeList =
            edges
            |> Seq.map (fun (from, to', attrs) ->
                { FromNode = from; ToNode = to'; Attributes = attrs })
            |> Seq.toList

        let graphAttrMap =
            graphAttrs
            |> Seq.fold (fun acc (k, v) -> Map.add k v acc) Map.empty

        { Name = name
          Nodes = nodeMap
          Edges = edgeList
          GraphAttributes = graphAttrMap }

    /// Parse a DOT source string into a Graph
    let parse (source: string) : Result<Graph, string> =
        try
            let tokens = Lexer.tokenize source
            let state =
                { Tokens = tokens |> Array.ofList
                  Pos = 0 }

            // Expect: digraph Name { ... }
            state.Expect(Token.Digraph)
            let name =
                match state.Current with
                | Token.Identifier id ->
                    state.Advance()
                    id
                | Token.QuotedString s ->
                    state.Advance()
                    s
                | _ -> "unnamed"
            state.Expect(Token.LBrace)
            let statements = parseStatements state
            state.Expect(Token.RBrace)
            let graph = buildGraph name statements
            Ok graph
        with ex ->
            Error ex.Message

    /// Parse a DOT source string, raising on error
    let parseOrRaise (source: string) : Graph =
        match parse source with
        | Ok graph -> graph
        | Error msg -> failwith msg
