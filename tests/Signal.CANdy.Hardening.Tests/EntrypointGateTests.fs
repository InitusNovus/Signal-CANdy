namespace Signal.CANdy.Hardening.Tests

open System.IO
open System.Text.RegularExpressions
open Xunit

module EntrypointGateTests =

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``PowerShell hardening surface parses and production entrypoint replays a pinned case`` () =
        let fixture =
            Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "hardening-surface.ps1")

        let parse =
            TestSupport.runProcess
                TestSupport.repoRoot
                "pwsh"
                [ "-NoProfile"
                  "-CommandWithArgs"
                  "$errors=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$errors); if($errors.Count){$errors | ForEach-Object { [Console]::Error.WriteLine($_) }; exit 1}"
                  fixture ]

        Assert.True(parse.ExitCode = 0, parse.StandardError)

        let production = Path.Combine(TestSupport.repoRoot, "scripts", "hardening.ps1")
        Assert.True(File.Exists(production), "scripts/hardening.ps1 is the intended RED production seam")

        // The full 10,000-case Ci pipeline is executed by the dedicated GitHub
        // Actions hardening job (asserted below) and is deliberately not rerun
        // here: a wall-clock-bounded rerun under parallel corpus load would be
        // a timing-flaky criterion, which issue #25 forbids. Replay of the
        // pinned regression case still exercises the production script,
        // tool invocation, base corpus loading, and replay machinery.
        let replay =
            TestSupport.runProcessWithTimeout
                300000
                TestSupport.repoRoot
                "pwsh"
                [ "-NoProfile"
                  "-File"
                  production
                  "-Mode"
                  "Replay"
                  "-CaseId"
                  "legacy-rx/field/sym.name.malformedUtf8/0127/12ce69eb8462c1d1" ]

        Assert.Equal(0, replay.ExitCode)
        Assert.Contains("legacy-rx/field/sym.name.malformedUtf8/0127", replay.StandardOutput)

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``GitHub CI requires the one hardening command and sanitizers`` () =
        let workflow =
            File.ReadAllText(Path.Combine(TestSupport.repoRoot, ".github", "workflows", "ci.yml"))

        let restore =
            "dotnet restore tools/Signal.CANdy.Hardening/Signal.CANdy.Hardening.fsproj --nologo"

        let hardening = "pwsh -NoProfile -File scripts/hardening.ps1 -Mode Ci"

        Assert.Contains("SC_HARDENING_REQUIRE_SANITIZERS: '1'", workflow)
        Assert.Contains(restore, workflow)
        Assert.Contains(hardening, workflow)

        Assert.True(
            workflow.IndexOf(restore) < workflow.IndexOf(hardening),
            "the hardening tool must be restored before its --no-restore build runs"
        )

    [<Fact>]
    let ``Release workflow publishes and releases from the selected tag`` () =
        let workflow =
            File.ReadAllText(Path.Combine(TestSupport.repoRoot, ".github", "workflows", "release.yml"))

        Assert.Contains("echo \"tag=$TAG_NAME\" >> $GITHUB_OUTPUT", workflow)
        Assert.Contains("echo \"prerelease=$IS_PRERELEASE\" >> $GITHUB_OUTPUT", workflow)
        Assert.Contains("if [[ \"$VERSION\" == *-* ]]; then", workflow)

        let stepBoundary = "(?:(?!\\r?\\n      - name:).)*?"

        let publishPattern =
            "(?s)if: \\$\\{\\{ steps\\.gate\\.outputs\\.skip == 'false' \\}\\}"
            + stepBoundary
            + "dotnet nuget push"

        Assert.Matches(Regex(publishPattern), workflow)

        let releasePattern prerelease =
            "(?s)if: \\$\\{\\{ steps\\.gate\\.outputs\\.skip == 'false' && steps\\.ver\\.outputs\\.prerelease == '"
            + prerelease
            + "' \\}\\}"
            + stepBoundary
            + "uses: softprops/action-gh-release@v2"
            + stepBoundary
            + "tag_name: \\$\\{\\{ steps\\.ver\\.outputs\\.tag \\}\\}"

        Assert.Matches(Regex(releasePattern "false"), workflow)
        Assert.Matches(Regex(releasePattern "true"), workflow)
