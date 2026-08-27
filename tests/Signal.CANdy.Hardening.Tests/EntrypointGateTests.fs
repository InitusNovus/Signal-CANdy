namespace Signal.CANdy.Hardening.Tests

open System.IO
open Xunit

module EntrypointGateTests =

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``PowerShell hardening surface parses and production Ci entrypoint exists`` () =
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

        let ci =
            TestSupport.runProcess TestSupport.repoRoot "pwsh" [ "-NoProfile"; "-File"; production; "-Mode"; "Ci" ]

        Assert.Equal(0, ci.ExitCode)

    [<Fact>]
    [<Trait("Issue25Gate", "Resources")>]
    let ``GitHub CI requires the one hardening command and sanitizers`` () =
        let workflow =
            File.ReadAllText(Path.Combine(TestSupport.repoRoot, ".github", "workflows", "ci.yml"))

        Assert.Contains("SC_HARDENING_REQUIRE_SANITIZERS: '1'", workflow)
        Assert.Contains("pwsh -NoProfile -File scripts/hardening.ps1 -Mode Ci", workflow)
