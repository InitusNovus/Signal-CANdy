open System
open Signal.CANdy.Core
open Signal.CANdy.Core.Errors

module Cli =
    type Parsed =
        { DbcPath: string option
          OutDir: string option
          ConfigPath: string option
          ShowVersion: bool
          ShowHelp: bool
          WithHarness: bool
          Unknown: string list }

    let empty: Parsed =
        { DbcPath = None
          OutDir = None
          ConfigPath = None
          ShowVersion = false
          ShowHelp = false
          WithHarness = false
          Unknown = [] }

    let usage () =
        String.concat "\n" [
            "Signal.CANdy.CLI — DBC → C code generator";
            "";
            "Usage:";
            "  signal-candy -d <file.dbc> -o <out_dir> [-c <config.yaml>] [-t]";
            "  signal-candy --version";
            "  signal-candy --help";
            "";
            "Options:";
            "  -d, --dbc <path>       Path to input DBC file (required)";
            "  -o, --out <dir>        Output directory for generated C files (required)";
            "  -c, --config <path>    Optional YAML config (phys_type, range_check, dispatch, etc.)";
            "  -t, --harness          Generate test harness files (main.c + Makefile) if missing";
            "  -v, --version          Print library version and exit";
            "  -h, --help             Show this help and exit"
        ]

    let parse (argv: string array): Parsed =
        let rec loop i (st: Parsed): Parsed =
            if i >= argv.Length then st
            else
                match argv.[i] with
                | "--dbc" when i + 1 < argv.Length -> loop (i + 2) { st with DbcPath = Some argv.[i + 1] }
                | "-d" when i + 1 < argv.Length -> loop (i + 2) { st with DbcPath = Some argv.[i + 1] }
                | "--out" when i + 1 < argv.Length -> loop (i + 2) { st with OutDir = Some argv.[i + 1] }
                | "-o" when i + 1 < argv.Length -> loop (i + 2) { st with OutDir = Some argv.[i + 1] }
                | "--config" when i + 1 < argv.Length -> loop (i + 2) { st with ConfigPath = Some argv.[i + 1] }
                | "-c" when i + 1 < argv.Length -> loop (i + 2) { st with ConfigPath = Some argv.[i + 1] }
                | "--harness" -> loop (i + 1) { st with WithHarness = true }
                | "-t" -> loop (i + 1) { st with WithHarness = true }
                | "--version" -> loop (i + 1) { st with ShowVersion = true }
                | "-v" -> loop (i + 1) { st with ShowVersion = true }
                | "--help" | "-h" -> loop (i + 1) { st with ShowHelp = true }
                | unk -> loop (i + 1) { st with Unknown = st.Unknown @ [ unk ] }
        loop 0 empty

[<EntryPoint>]
let main argv: int =
    let args = Cli.parse argv

    if args.ShowHelp then
        printfn "%s" (Cli.usage ())
        0
    elif args.ShowVersion then
        printfn "%s" (Signal.CANdy.Core.Api.version ())
        0
    elif args.Unknown |> List.isEmpty |> not then
        eprintfn "Unknown arguments: %s" (String.Join(" ", args.Unknown))
        eprintfn "\n%s" (Cli.usage ())
        2
    else
        match args.DbcPath, args.OutDir with
        | Some dbc, Some outDir ->
            try
                let cfgOpt = args.ConfigPath
                let t = Signal.CANdy.Core.Api.generateFromPaths dbc outDir cfgOpt
                let res = t.GetAwaiter().GetResult()
                match res with
                | Ok files ->
                    printfn "Code generation successful."
                    printfn "Headers: %d, Sources: %d, Others: %d" (files.Headers.Length) (files.Sources.Length) (files.Others.Length)
                    // Optionally generate test harness if requested
                    if args.WithHarness then
                        try
                            let outDirFull = System.IO.Path.GetFullPath(outDir)
                            let srcDir = System.IO.Path.Combine(outDirFull, "src")
                            let includeDir = System.IO.Path.Combine(outDirFull, "include")
                            System.IO.Directory.CreateDirectory(srcDir) |> ignore
                            System.IO.Directory.CreateDirectory(includeDir) |> ignore

                            // Try to copy examples/main.c if available and not already present
                            let mainDst = System.IO.Path.Combine(srcDir, "main.c")
                            if not (System.IO.File.Exists(mainDst)) then
                                let candidates = [
                                    System.IO.Path.Combine("examples", "main.c")
                                    System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "examples", "main.c")
                                ]
                                let found = candidates |> List.tryFind System.IO.File.Exists
                                match found with
                                | Some src -> System.IO.File.Copy(src, mainDst, true)
                                | None -> eprintfn "Warning: examples/main.c not found; skipping main.c copy."

                            // Harmonize includes in harness sources to match available headers (handles prefixed utils/registry)
                            let chooseHeader (pattern: string) (fallback: string) =
                                try
                                    let files = System.IO.Directory.GetFiles(includeDir, pattern) |> Array.toList
                                    match files |> List.map System.IO.Path.GetFileName with
                                    | [] -> fallback
                                    | names ->
                                        // Prefer prefixed variants like "test_utils.h" over generic "utils.h"
                                        match names |> List.filter (fun n -> not (n.Equals(fallback, StringComparison.OrdinalIgnoreCase))) with
                                        | pref::_ -> pref
                                        | [] -> fallback
                                with _ -> fallback

                            let utilsHeader = chooseHeader "*utils.h" "utils.h"
                            let registryHeader = chooseHeader "*registry.h" "registry.h"

                            let tryRewriteIncludes (path: string) =
                                try
                                    if System.IO.File.Exists(path) then
                                        let text = System.IO.File.ReadAllText(path)
                                        let replaced =
                                            text
                                                .Replace("\"sc_utils.h\"", $"\"{utilsHeader}\"")
                                                .Replace("\"sc_registry.h\"", $"\"{registryHeader}\"")
                                                // If a different prefixed header exists, normalize generic includes too
                                                .Replace("\"utils.h\"", $"\"{utilsHeader}\"")
                                                .Replace("\"registry.h\"", $"\"{registryHeader}\"")
                                        if replaced <> text then System.IO.File.WriteAllText(path, replaced)
                                with ex -> eprintfn "Harness include rewrite warning for %s: %s" path ex.Message

                            tryRewriteIncludes(mainDst)
                            // Also adapt fixed_test.c if present
                            let fixedTest = System.IO.Path.Combine(srcDir, "fixed_test.c")
                            tryRewriteIncludes(fixedTest)

                            // Patch generated common sources to include the available headers
                            let patchCommonSource (namePattern: string) (expectedHeader: string) =
                                try
                                    let files = System.IO.Directory.GetFiles(srcDir, namePattern)
                                    for f in files do
                                        let text = System.IO.File.ReadAllText(f)
                                        let replaced =
                                            text
                                                .Replace("\"sc_" + expectedHeader + "\"", $"\"{expectedHeader}\"")
                                                .Replace($"\"{expectedHeader}\"", $"\"{expectedHeader}\"")
                                                .Replace("\"sc_utils.h\"", $"\"{utilsHeader}\"")
                                                .Replace("\"sc_registry.h\"", $"\"{registryHeader}\"")
                                        if replaced <> text then System.IO.File.WriteAllText(f, replaced)
                                with ex -> eprintfn "Harness common source rewrite warning: %s" ex.Message

                            patchCommonSource "*utils.c" utilsHeader
                            patchCommonSource "*registry.c" registryHeader

                            // Create or upgrade a Makefile to adapt to whatever files were generated
                            let mkPath = System.IO.Path.Combine(outDirFull, "Makefile")
                            let mk = """
# Signal.CANdy harness Makefile v1
# Auto-generated by Signal.CANdy.CLI when using --harness (-t).
# It discovers sources under ./src dynamically, so it should work regardless of generated filenames.
# If this file already existed, the CLI will back it up to Makefile.bak before upgrading.

CC ?= gcc
CFLAGS ?= -Wall -Wextra -std=c99
EXTRA_CFLAGS ?=
LDLIBS ?= -lm

BUILD_DIR = build
SRC_DIR = src
INCLUDE_DIR = include

# Discover all C sources under src and map to objects under build
SRCS := $(wildcard $(SRC_DIR)/*.c)
# Add stress test source if available
ifneq (,$(wildcard ../examples/stress_test.c))
SRCS += ../examples/stress_test.c
CFLAGS += -DHAVE_STRESS
endif

# If any prefixed common files exist, drop legacy unprefixed ones to avoid duplicates
ifneq (,$(filter-out $(SRC_DIR)/registry.c,$(wildcard $(SRC_DIR)/*registry.c)))
SRCS := $(filter-out $(SRC_DIR)/registry.c,$(SRCS))
endif
ifneq (,$(filter-out $(SRC_DIR)/utils.c,$(wildcard $(SRC_DIR)/*utils.c)))
SRCS := $(filter-out $(SRC_DIR)/utils.c,$(SRCS))
endif

# If multiple prefixed variants exist (e.g., sc_utils.c and test_utils.c),
# keep only one deterministically (lexicographically first) to avoid duplicate symbols.
UTIL_SRCS := $(wildcard $(SRC_DIR)/*utils.c)
ifneq ($(strip $(UTIL_SRCS)),)
PRIMARY_UTIL := $(word 1,$(sort $(UTIL_SRCS)))
SRCS := $(filter-out $(filter-out $(PRIMARY_UTIL),$(UTIL_SRCS)),$(SRCS))
endif

REG_SRCS := $(wildcard $(SRC_DIR)/*registry.c)
ifneq ($(strip $(REG_SRCS)),)
PRIMARY_REG := $(word 1,$(sort $(REG_SRCS)))
SRCS := $(filter-out $(filter-out $(PRIMARY_REG),$(REG_SRCS)),$(SRCS))
endif
OBJS := $(patsubst $(SRC_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRCS))
OBJS := $(patsubst ../examples/%.c,$(BUILD_DIR)/%.o,$(OBJS))

TARGET = $(BUILD_DIR)/test_runner

.PHONY: all build test clean

all: build

build: $(TARGET)

# Generic build rule for any C file in src/
$(BUILD_DIR)/%.o: $(SRC_DIR)/%.c
	mkdir -p $(@D)
	$(CC) $(CFLAGS) $(EXTRA_CFLAGS) -I$(INCLUDE_DIR) -c $< -o $@

# Rule for examples directory
$(BUILD_DIR)/%.o: ../examples/%.c
	mkdir -p $(@D)
	$(CC) $(CFLAGS) $(EXTRA_CFLAGS) -I$(INCLUDE_DIR) -c $< -o $@

# Link all objects into the test runner
$(TARGET): $(OBJS)
	mkdir -p $(@D)
	$(CC) $(CFLAGS) $(EXTRA_CFLAGS) $(OBJS) $(LDLIBS) -o $@

# Placeholder test target (adjust as needed)
test:
	@echo "Running C tests... (Placeholder)"
	@echo "No actual tests implemented yet. This is a placeholder."
	# For example: ./$(TARGET) test_be_basic

clean:
	rm -rf $(BUILD_DIR)
"""
                            let writeMk () = System.IO.File.WriteAllText(mkPath, mk)
                            if System.IO.File.Exists(mkPath) then
                                try
                                    let existing = System.IO.File.ReadAllText(mkPath)
                                    if existing.Contains("Signal.CANdy harness Makefile v1") then
                                        if existing <> mk then
                                            writeMk()
                                            printfn "Harness: Makefile updated to latest template."
                                        else
                                            printfn "Harness: Makefile already up-to-date."
                                    else
                                        let bak = mkPath + ".bak"
                                        System.IO.File.Copy(mkPath, bak, true)
                                        writeMk()
                                        printfn "Harness: Makefile upgraded with backup at %s" bak
                                with ex ->
                                    eprintfn "Harness Makefile upgrade warning: %s" ex.Message
                            else
                                writeMk()
                                printfn "Harness: Makefile created at %s" mkPath

                            // Provide compatibility alias headers so legacy test sources that include sc_*.h keep building
                            let tryCreateAlias (pattern: string) (aliasName: string) =
                                try
                                    let headers = System.IO.Directory.GetFiles(includeDir, pattern) |> Array.sort
                                    if headers.Length > 0 then
                                        let primary = System.IO.Path.GetFileName(headers.[0])
                                        let aliasPath = System.IO.Path.Combine(includeDir, aliasName)
                                        if not (System.IO.File.Exists(aliasPath)) then
                                            let content = sprintf "/* Auto-generated alias for harness compatibility */\n#include \"%s\"\n" primary
                                            System.IO.File.WriteAllText(aliasPath, content)
                                            printfn "Harness: Created alias header %s -> %s" aliasName primary
                                with ex ->
                                    eprintfn "Harness alias header warning (%s): %s" aliasName ex.Message

                            // sc_utils.h -> <prefix>utils.h (if sc_utils.h missing)
                            tryCreateAlias "*utils.h" "sc_utils.h"
                            // sc_registry.h -> <prefix>registry.h (if sc_registry.h missing)
                            tryCreateAlias "*registry.h" "sc_registry.h"
                        with ex ->
                            eprintfn "Harness generation warning: %s" ex.Message
                    0
                | Error err ->
                    let msg =
                        match err with
                        | CodeGenError.TemplateError s -> sprintf "Template error: %s" s
                        | CodeGenError.IoError s -> sprintf "IO error: %s" s
                        | CodeGenError.Unknown s -> sprintf "Error: %s" s
                    eprintfn "%s" msg
                    1
            with ex ->
                eprintfn "Unhandled error: %s" ex.Message
                1
        | _ ->
            eprintfn "Missing required arguments."
            eprintfn "\n%s" (Cli.usage ())
            2
