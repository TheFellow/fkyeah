namespace Attractor

open System
open AcpRuntime

module AcpPresets =

    type PresetKind =
        | Codex
        | ClaudeCode
        | Gemini

        static member Parse(value: string) =
            if String.IsNullOrWhiteSpace(value) then
                None
            else
                match value.Trim().ToLowerInvariant() with
                | "codex" -> Some Codex
                | "claude"
                | "claude-code"
                | "claude_code" -> Some ClaudeCode
                | "gemini"
                | "gemini-cli"
                | "gemini_cli" -> Some Gemini
                | _ -> None

    type PresetConfig =
        { Command: string
          Args: string list
          WorkingDirectory: string
          TimeoutMs: int
          Transport: AcpTransportKind }

    let private envOr (key: string) (fallback: string) =
        match Environment.GetEnvironmentVariable(key) with
        | null
        | "" -> fallback
        | value -> value

    let private envIntOr (key: string) (fallback: int) =
        match Environment.GetEnvironmentVariable(key) with
        | null
        | "" -> fallback
        | value ->
            match Int32.TryParse(value) with
            | true, parsed -> parsed
            | _ -> fallback

    let resolve (kind: PresetKind) (workingDir: string) : PresetConfig =
        match kind with
        | Codex ->
            { Command = envOr "ATTRACTOR_CODEX_ACP_AGENT_BIN" "codex"
              Args = [ "exec"; "-m"; envOr "ATTRACTOR_CODEX_MODEL" "gpt-5.5" ]
              WorkingDirectory = envOr "ATTRACTOR_CODEX_ACP_CWD" workingDir
              TimeoutMs = envIntOr "ATTRACTOR_CODEX_ACP_TIMEOUT_SECONDS" (120 * 1000)
              Transport = AcpTransportKind.Stdio }
        | ClaudeCode ->
            { Command = envOr "ATTRACTOR_CLAUDE_ACP_AGENT_BIN" "claude"
              Args = [ "--dangerously-skip-permissions" ]
              WorkingDirectory = envOr "ATTRACTOR_CLAUDE_ACP_CWD" workingDir
              TimeoutMs = envIntOr "ATTRACTOR_CLAUDE_ACP_TIMEOUT_SECONDS" (180 * 1000)
              Transport = AcpTransportKind.Stdio }
        | Gemini ->
            { Command = envOr "ATTRACTOR_GEMINI_ACP_AGENT_BIN" "gemini"
              Args = [ "--yolo" ]
              WorkingDirectory = envOr "ATTRACTOR_GEMINI_ACP_CWD" workingDir
              TimeoutMs = envIntOr "ATTRACTOR_GEMINI_ACP_TIMEOUT_SECONDS" (120 * 1000)
              Transport = AcpTransportKind.Stdio }

    let toEndpoint (preset: PresetConfig) : AcpEndpoint =
        { Transport = preset.Transport
          Command = Some preset.Command
          Args = preset.Args
          Url = None
          Headers = Map.empty
          WorkingDirectory = Some preset.WorkingDirectory }
