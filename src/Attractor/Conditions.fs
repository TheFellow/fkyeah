namespace Attractor

open System

/// Condition expression language for edge guards
module Conditions =

    let private parseLiteral (literal: string) : string =
        let trimmed = literal.Trim()

        if
            trimmed.Length >= 2
            && trimmed.StartsWith("\"", StringComparison.Ordinal)
            && trimmed.EndsWith("\"", StringComparison.Ordinal)
        then
            trimmed.Substring(1, trimmed.Length - 2)
        else
            trimmed

    /// Resolve a key to a string value from outcome and context
    let resolveKey (key: string) (outcome: Outcome) (context: Context) : string =
        match key.Trim() with
        | "outcome" -> outcome.OutcomeString
        | "preferred_label" -> outcome.PreferredLabel
        | k when k.StartsWith("context.", StringComparison.Ordinal) ->
            let contextKey = k

            match context.TryGet(contextKey) with
            | Some v -> v
            | None ->
                // Try without context. prefix
                let stripped = k.Substring(8) // len of "context."

                match context.TryGet(stripped) with
                | Some v -> v
                | None -> ""
        | k ->
            // Direct context lookup
            match context.TryGet(k) with
            | Some v -> v
            | None -> ""

    /// Evaluate a single clause like "outcome=success" or "outcome!=fail"
    let evaluateClause (clause: string) (outcome: Outcome) (context: Context) : bool =
        let clause = clause.Trim()

        if String.IsNullOrWhiteSpace(clause) then
            true
        elif clause.Contains("!=") then
            let parts = clause.Split("!=", 2, StringSplitOptions.None)

            if parts.Length = 2 then
                let key = parts[0].Trim()
                let value = parseLiteral parts[1]
                resolveKey key outcome context <> value
            else
                false
        elif clause.Contains("==") then
            let parts = clause.Split("==", 2, StringSplitOptions.None)

            if parts.Length = 2 then
                let key = parts[0].Trim()
                let value = parseLiteral parts[1]
                resolveKey key outcome context = value
            else
                false
        elif clause.Contains("=") then
            let parts = clause.Split("=", 2, StringSplitOptions.None)

            if parts.Length = 2 then
                let key = parts[0].Trim()
                let value = parseLiteral parts[1]
                resolveKey key outcome context = value
            else
                false
        else
            // Bare key: check if truthy
            let value = resolveKey clause outcome context
            not (String.IsNullOrEmpty(value)) && value <> "false" && value <> "0"

    /// Evaluate a full condition expression (clauses joined by &&)
    let evaluate (condition: string) (outcome: Outcome) (context: Context) : bool =
        if String.IsNullOrWhiteSpace(condition) then
            true
        else
            condition.Split("&&")
            |> Array.forall (fun clause -> evaluateClause clause outcome context)

    /// Validate that a condition expression is syntactically correct
    let validate (condition: string) : Result<unit, string> =
        if String.IsNullOrWhiteSpace(condition) then
            Ok()
        else
            try
                let clauses = condition.Split("&&")

                for clause in clauses do
                    let c = clause.Trim()

                    if not (String.IsNullOrWhiteSpace(c)) then
                        if c.Contains("!=") then
                            let parts = c.Split("!=", 2, StringSplitOptions.None)

                            if parts.Length <> 2 || String.IsNullOrWhiteSpace(parts[0]) then
                                failwith $"Invalid clause: {c}"
                        elif c.Contains("==") then
                            let parts = c.Split("==", 2, StringSplitOptions.None)

                            if parts.Length <> 2 || String.IsNullOrWhiteSpace(parts[0]) then
                                failwith $"Invalid clause: {c}"
                        elif c.Contains("=") then
                            let parts = c.Split("=", 2, StringSplitOptions.None)

                            if parts.Length <> 2 || String.IsNullOrWhiteSpace(parts[0]) then
                                failwith $"Invalid clause: {c}"
                // else it's a bare key, which is valid
                Ok()
            with ex ->
                Error ex.Message
