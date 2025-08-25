namespace Generator.Tests

open Xunit
open FsUnit.Xunit
open Generator
open Generator.Dbc
open Generator.Result
open System.IO
open System.Diagnostics
open System

module CodegenTests = 

    [<Fact>]
    let ``Sample test stub`` () =
        true |> should be True

    [<Fact>]
    let ``DBC parsing and IR generation test`` () =
        let dbcPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "sample.dbc")
        let result = Dbc.parseDbcFile dbcPath
        match result with 
        | Success ir ->
            ir.Messages |> should not' (be Empty)
            ir.Messages.Length |> should equal 1
            ir.Messages.[0].Name |> should equal "MESSAGE_1"
            ir.Messages.[0].Signals.Length |> should equal 2
        | Failure errors ->
            failwith (sprintf "Expected success, but got errors: %A" errors)

    // Ensure a Makefile exists in the generated output directory (copy from repo's gen/Makefile)
    let ensureMakefile (genOutputPath: string) =
        let repoMakefile = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "gen", "Makefile")
        let outMakefile = Path.Combine(genOutputPath, "Makefile")
        if File.Exists(repoMakefile) then
            File.Copy(repoMakefile, outMakefile, true)
        else
            // Fallback: write a minimal Makefile that can build the generated C code
            let makefileTemplate = """
CC = gcc
CFLAGS = -Wall -Wextra -std=c99
LDLIBS = -lm

# Avoid TAB requirement for recipes (GNU make >= 3.82)
.RECIPEPREFIX := >

BUILD_DIR = build
SRC_DIR = src
INCLUDE_DIR = include

# Discover all C sources under src
SRCS := $(wildcard $(SRC_DIR)/*.c)
# If any prefixed common files exist, drop legacy unprefixed ones to avoid duplicates
ifneq (,$(filter-out $(SRC_DIR)/registry.c,$(wildcard $(SRC_DIR)/*registry.c)))
SRCS := $(filter-out $(SRC_DIR)/registry.c,$(SRCS))
endif
ifneq (,$(filter-out $(SRC_DIR)/utils.c,$(wildcard $(SRC_DIR)/*utils.c)))
SRCS := $(filter-out $(SRC_DIR)/utils.c,$(SRCS))
endif

OBJS := $(patsubst $(SRC_DIR)/%.c,$(BUILD_DIR)/%.o,$(SRCS))

TARGET = $(BUILD_DIR)/test_runner

.PHONY: all build clean

all: build

build: $(TARGET)

$(BUILD_DIR)/%.o: $(SRC_DIR)/%.c
>mkdir -p $(@D)
>$(CC) $(CFLAGS) -I$(INCLUDE_DIR) -c $< -o $@

$(TARGET): $(OBJS)
>mkdir -p $(@D)
>$(CC) $(CFLAGS) $(OBJS) $(LDLIBS) -o $@

clean:
>rm -rf $(BUILD_DIR)
"""
            File.WriteAllText(outMakefile, makefileTemplate)

    let runCGenerator (configPath: string option) (genOutputPath: string) =
        let dbcPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "sample.dbc")
        let configArg =
            match configPath with
            | Some p -> sprintf "--config %s" p
            | None -> ""
        let args = sprintf "--dbc %s --out %s %s" dbcPath genOutputPath configArg
        let proc = new Process()
        proc.StartInfo.FileName <- "dotnet"
        let generatorPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Generator", "Generator.fsproj")
        proc.StartInfo.Arguments <- sprintf "run --project %s -- %s" generatorPath args
        proc.StartInfo.UseShellExecute <- false
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        proc.Start() |> ignore
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then
            failwith (sprintf "Generator failed with exit code %d.\nStdout:\n%s\nStderr:\n%s" proc.ExitCode stdout stderr)
        // Copy Makefile after successful generation
        ensureMakefile genOutputPath
        ()

    let buildAndRunCTest (genOutputPath: string) (cTestName: string) : string list =
        // Build using the Makefile in genOutputPath
        let make = new Process()
        make.StartInfo.FileName <- "make"
        make.StartInfo.Arguments <- sprintf "-C \"%s\" build" genOutputPath
        make.StartInfo.UseShellExecute <- false
        make.StartInfo.RedirectStandardOutput <- true
        make.StartInfo.RedirectStandardError <- true
        make.Start() |> ignore
        let makeStdout = make.StandardOutput.ReadToEnd()
        let makeStderr = make.StandardError.ReadToEnd()
        make.WaitForExit()
        if make.ExitCode <> 0 then
            failwith (sprintf "Make build failed with exit code %d.\nStdout:\n%s\nStderr:\n%s" make.ExitCode makeStdout makeStderr)

        // Run test
        let run = new Process()
        run.StartInfo.FileName <- Path.Combine(genOutputPath, "build", "test_runner")
        run.StartInfo.Arguments <- cTestName
        run.StartInfo.UseShellExecute <- false
        run.StartInfo.RedirectStandardOutput <- true
        run.Start() |> ignore
        let out = run.StandardOutput.ReadToEnd()
        run.WaitForExit()
        if run.ExitCode <> 0 then
            failwith (sprintf "C test runner failed with exit code %d" run.ExitCode)
        out.Split([|'\r';'\n'|], System.StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray

    [<Fact>]
    let ``Encode/Decode roundtrip for SimpleMessage`` () =
        let genOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(genOutputPath) |> ignore
        try
            // default config (range_check=false)
            runCGenerator None genOutputPath
            let output = buildAndRunCTest genOutputPath "test_roundtrip"
            output |> should contain "Roundtrip successful!"
        finally
            if Directory.Exists(genOutputPath) then
                Directory.Delete(genOutputPath, true)

    [<Fact>]
    let ``Roundtrip with fixed phys_type`` () =
        let genOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(genOutputPath) |> ignore
        try
            let configPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "config_fixed.yaml")
            runCGenerator (Some configPath) genOutputPath
            let output = buildAndRunCTest genOutputPath "test_roundtrip"
            output |> should contain "Roundtrip successful!"
        finally
            if Directory.Exists(genOutputPath) then
                Directory.Delete(genOutputPath, true)

    [<Fact>]
    let ``Range check test`` () =
        let genOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(genOutputPath) |> ignore
        try
            let configPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "config_range_check.yaml")
            runCGenerator (Some configPath) genOutputPath
            let output = buildAndRunCTest genOutputPath "test_range_check"
            output |> should contain "Range check test successful!"
        finally
            if Directory.Exists(genOutputPath) then
                Directory.Delete(genOutputPath, true)

    [<Fact>]
    let ``Dispatch direct_map test`` () =
        let genOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(genOutputPath) |> ignore
        try
            let configPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "config_direct_map.yaml")
            runCGenerator (Some configPath) genOutputPath
            let output = buildAndRunCTest genOutputPath "test_dispatch"
            output |> should contain "Dispatch successful for message ID 100"
            output |> should contain "Dispatch correctly failed for unknown message ID 99"
        finally
            if Directory.Exists(genOutputPath) then
                Directory.Delete(genOutputPath, true)

    [<Fact>]
    let ``CRC and Counter check test`` () =
        let genOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(genOutputPath) |> ignore
        try
            let configPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "config_crc_counter.yaml")
            runCGenerator (Some configPath) genOutputPath
            let output = buildAndRunCTest genOutputPath "test_crc_counter"
            output |> should not' (be Null)
            output |> should not' (be Empty)
        finally
            if Directory.Exists(genOutputPath) then
                Directory.Delete(genOutputPath, true)

    [<Fact>]
    let ``DBC signal field mapping correctness`` () =
        let dbcPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "sample.dbc")
        let result = Dbc.parseDbcFile dbcPath
        match result with 
        | Success ir ->
            let msg = ir.Messages |> List.exactlyOne
            msg.Id |> should equal 100u
            msg.Length |> should equal 8us
            msg.Sender |> should equal "Vector__XXX"
            let s1 = msg.Signals |> List.find (fun s -> s.Name = "Signal_1")
            s1.StartBit |> should equal 0us
            s1.Length |> should equal 8us
            s1.Factor |> should equal 1.0
            s1.Offset |> should equal 0.0
            s1.Minimum |> should equal (Some 0.0)
            s1.Maximum |> should equal (Some 255.0)
            s1.Unit |> should equal ""
            let s2 = msg.Signals |> List.find (fun s -> s.Name = "Signal_2")
            s2.StartBit |> should equal 8us
            s2.Length |> should equal 16us
            s2.Factor |> should equal 0.1
            s2.Offset |> should equal 0.0
            s2.Minimum |> should equal (Some 0.0)
            s2.Maximum |> should equal (Some 100.0)
            s2.Unit |> should equal "Unit"
        | Failure errors ->
            failwith (sprintf "Expected success, but got errors: %A" errors)

    [<Fact>]
    let ``DBC parsing with invalid file path`` () =
        let dbcPath = "non_existent_file.dbc"
        let result = Dbc.parseDbcFile dbcPath
        match result with 
        | Success _ -> failwith "Expected failure, but got success"
        | Failure _ -> true |> should be True
