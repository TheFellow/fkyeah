namespace Attractor

/// Public API surface for the Attractor pipeline engine
module Pipeline =

    /// Parse a DOT source string into a Graph
    let parse = DotParser.parse

    /// Parse a DOT source string, raising on error
    let parseOrRaise = DotParser.parseOrRaise

    /// Validate a graph and return diagnostics
    let validate graph = Validation.validate graph None

    /// Validate a graph and raise on errors
    let validateOrRaise graph = Validation.validateOrRaise graph None

    /// Apply built-in transforms (variable expansion, stylesheet)
    let prepare source = Transforms.preparePipeline source None

    /// Run a pipeline from a parsed graph
    let run = Engine.run

    /// Parse, validate, and run a pipeline from DOT source
    let runFromSource = Engine.runFromSource
