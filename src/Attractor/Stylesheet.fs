namespace Attractor

open System
open System.Text.RegularExpressions

/// Model stylesheet parser and applicator
module Stylesheet =

    /// Known shape names for shape-based selectors
    let private knownShapes =
        set [ "box"; "Mdiamond"; "Msquare"; "hexagon"; "diamond";
              "component"; "tripleoctagon"; "parallelogram"; "house" ]

    /// Selector types with specificity
    /// Specificity: universal (0) < shape (1) < class (2) < id (3)
    [<RequireQualifiedAccess>]
    type Selector =
        | Universal               // * -> specificity 0
        | Shape of string         // bare shape name (e.g., box) -> specificity 1
        | Class of string         // .className -> specificity 2
        | Id of string            // #nodeId -> specificity 3

        member this.Specificity =
            match this with
            | Universal -> 0
            | Shape _ -> 1
            | Class _ -> 2
            | Id _ -> 3

        member this.Matches(node: Node) =
            match this with
            | Universal -> true
            | Shape shape -> node.Shape = shape
            | Class cls ->
                let nodeClasses =
                    node.Class.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun s -> s.Trim())
                nodeClasses |> Array.exists (fun c -> c = cls)
            | Id id -> node.Id = id

    /// A single property declaration
    type Declaration =
        { Property: string
          Value: string }

    /// A stylesheet rule (selector + declarations)
    type Rule =
        { Selector: Selector
          Declarations: Declaration list }

    /// Parsed stylesheet
    type ParsedStylesheet =
        { Rules: Rule list }

    let private parseSelector (s: string) : Selector =
        let s = s.Trim()
        if s = "*" then Selector.Universal
        elif s.StartsWith(".") then Selector.Class(s.Substring(1))
        elif s.StartsWith("#") then Selector.Id(s.Substring(1))
        elif knownShapes.Contains(s) || Regex.IsMatch(s, @"^[a-zA-Z]\w*$") then
            // Bare identifier that could be a shape name
            if knownShapes.Contains(s) then Selector.Shape(s)
            else Selector.Shape(s) // treat any bare word as a shape selector
        else Selector.Universal

    let private parseDeclarations (block: string) : Declaration list =
        block.Split(';', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun decl ->
            let parts = decl.Split(':', 2, StringSplitOptions.None)
            if parts.Length = 2 then
                Some { Property = parts[0].Trim(); Value = parts[1].Trim() }
            else
                None)
        |> Array.toList

    /// Parse a CSS-like stylesheet string
    let parse (source: string) : Result<ParsedStylesheet, string> =
        if String.IsNullOrWhiteSpace(source) then
            Ok { Rules = [] }
        else
            try
                let rules = ResizeArray<Rule>()
                // Match pattern: selector { declarations }
                let regex = Regex(@"([*#.]?[\w-]*)\s*\{([^}]*)\}", RegexOptions.Singleline)
                let matches = regex.Matches(source)
                for m in matches do
                    let selector = parseSelector (m.Groups[1].Value)
                    let declarations = parseDeclarations (m.Groups[2].Value)
                    rules.Add({ Selector = selector; Declarations = declarations })
                Ok { Rules = rules |> Seq.toList }
            with ex ->
                Error ex.Message

    /// Validate stylesheet syntax
    let validate (source: string) : Result<unit, string> =
        match parse source with
        | Ok _ -> Ok()
        | Error msg -> Error msg

    /// Apply a parsed stylesheet to a graph, setting properties on nodes
    /// that don't have explicit overrides
    let apply (stylesheet: ParsedStylesheet) (graph: Graph) : Graph =
        let properties = set [ "llm_model"; "llm_provider"; "reasoning_effort" ]

        let updatedNodes =
            graph.Nodes
            |> Map.map (fun _ node ->
                // Remember which properties are explicitly set on the node (highest precedence)
                let explicitProps =
                    node.Attributes
                    |> Map.toList
                    |> List.filter (fun (k, _) -> properties.Contains(k))
                    |> List.map fst
                    |> set

                // Collect matching rules, sorted by specificity (lowest first so highest wins last)
                let matchingRules =
                    stylesheet.Rules
                    |> List.filter (fun r -> r.Selector.Matches(node))
                    |> List.sortBy (fun r -> r.Selector.Specificity)

                // Build a map of property -> value from matching rules (highest specificity wins)
                let stylesheetValues =
                    matchingRules
                    |> List.fold (fun acc rule ->
                        rule.Declarations
                        |> List.fold (fun a decl ->
                            if properties.Contains(decl.Property) then
                                Map.add decl.Property decl.Value a
                            else a
                        ) acc
                    ) Map.empty

                // Merge: explicit node attrs have highest precedence, then stylesheet
                let newAttrs =
                    stylesheetValues
                    |> Map.fold (fun attrs prop value ->
                        if explicitProps.Contains(prop) then attrs
                        else Map.add prop (AttrValue.String value) attrs
                    ) node.Attributes

                { node with Attributes = newAttrs })

        { graph with Nodes = updatedNodes }
