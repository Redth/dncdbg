"""Black-box contracts for the real suite scripts, with no debugger or SDK required."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


def dotnet_stub(arguments):
    with open(os.environ["RUNNER_TRACE"], "a", encoding="utf-8") as trace:
        trace.write(json.dumps(arguments, ensure_ascii=False) + "\n")
    if arguments[0] == "build":
        project = next((Path(arg).stem for arg in arguments[1:]
                        if Path(arg).stem == "Runner" or Path(arg).stem.startswith("Test")), "")
        if project != "Runner" and not Path(project, project + ".csproj").is_file():
            return 7
        return 7 if os.environ.get("RUNNER_FAIL") == "build:" + project else 0
    if arguments[0] == "run":
        project = arguments[arguments.index("--test") + 1]
        return 9 if os.environ.get("RUNNER_FAIL") == "run:" + project else 0
    raise ValueError("Unexpected dotnet invocation: " + repr(arguments))


if __name__ == "__main__" and sys.argv[1:2] == ["--dotnet-stub"]:
    sys.exit(dotnet_stub(sys.argv[2:]))


ROOT = Path(__file__).resolve().parents[1]
OPTIONS = None
PORTABLE = ["TestAlpha", "TestBeta", "TestOmega_Release",
            "TestServerCustomPort", "TestServerDefaultPort", "Test\u00dcnicode", "TestZulu"]
WINDOWS = ["TestWindowsForms", "TestWindowsTarget"]


class SuiteRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="dncdbg-runner-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "fixture tree"
        self.suite = self.root / "test-suite"
        self.suite.mkdir(parents=True)
        self.script = self.suite / Path(OPTIONS.script).name
        shutil.copyfile(OPTIONS.script, self.script)
        (self.suite / "Runner").mkdir()
        (self.suite / "Runner/Runner.csproj").touch()
        (self.root / "bin").mkdir()
        self.debugger = self.root / "bin" / ("dncdbg-dap.exe" if os.name == "nt" else "dncdbg-dap")
        self.debugger.touch()
        for name in PORTABLE + WINDOWS:
            project = self.suite / name
            project.mkdir()
            framework = "net10.0-windows" if name == "TestWindowsTarget" else "net10.0"
            forms = "<UseWindowsForms>true</UseWindowsForms>" if name == "TestWindowsForms" else ""
            (project / (name + ".csproj")).write_text(
                f"<Project><PropertyGroup><TargetFramework>{framework}</TargetFramework>"
                f"{forms}</PropertyGroup></Project>", encoding="utf-8")
            (project / "Program.cs").write_text("// source\n", encoding="utf-8")
            (project / "Nested").mkdir()
            (project / "Nested" / "Unicode \u00fc.cs").write_text("// nested source\n", encoding="utf-8")
        tools = self.root / "tools"
        tools.mkdir()
        if os.name == "nt":
            (tools / "dotnet.cmd").write_text(
                f'@"{sys.executable}" "{Path(__file__).resolve()}" --dotnet-stub %*\n',
                encoding="utf-8")
        else:
            stub = tools / "dotnet"
            stub.write_text(
                "#!/bin/sh\nexec " + shlex.quote(sys.executable) + " " +
                shlex.quote(str(Path(__file__).resolve())) + ' --dotnet-stub "$@"\n',
                encoding="utf-8")
            stub.chmod(0o755)
        self.trace = self.root / "trace.jsonl"
        self.env = os.environ.copy()
        for name in ("DNCDBG_TARGET_FRAMEWORK", "RUNNER_FAIL"):
            self.env.pop(name, None)
        self.env.update(PATH=str(tools) + os.pathsep + os.environ["PATH"],
                        DNCDBG=str(self.debugger), RUNNER_TRACE=str(self.trace), TERM="dumb")
        if OPTIONS.kind == "native-sh":
            # Bash glob order has its own locale contract, not PowerShell's comparer.
            self.env["LC_ALL"] = "C"
        self.calls = []
        self.output = ""
        self.invocations = 0

    @property
    def managed(self):
        return OPTIONS.kind == "managed"

    @property
    def native_windows(self):
        return os.name == "nt" and OPTIONS.kind != "native-sh"

    @property
    def available(self):
        names = PORTABLE + (WINDOWS if os.name == "nt" else [])
        if OPTIONS.kind == "native-sh":
            return sorted(names)
        return [name for name in OPTIONS.project_order if name in names]

    def invoke(self, *arguments, failure=None, framework=None, success=True, powershell_array=False):
        if self.trace.exists():
            self.trace.unlink()
        if failure is not None:
            self.env["RUNNER_FAIL"] = failure
        if framework is not None:
            self.env["DNCDBG_TARGET_FRAMEWORK"] = framework
        command = (["bash", str(self.script)] if OPTIONS.kind == "native-sh" else
                   OPTIONS.pwsh + ["-NoLogo", "-NoProfile", "-File", str(self.script)])
        if powershell_array:
            self.env.update(RUNNER_SCRIPT=str(self.script), RUNNER_ARGUMENTS=json.dumps(arguments))
            command = OPTIONS.pwsh + [
                "-NoLogo", "-NoProfile", "-Command",
                "$scriptArguments = @(ConvertFrom-Json $env:RUNNER_ARGUMENTS); "
                "& $env:RUNNER_SCRIPT @scriptArguments"]
        invocation = command + ([] if powershell_array else list(arguments))
        result = subprocess.run(invocation, cwd=self.suite, env=self.env,
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.output = result.stdout + result.stderr
        self.calls = [json.loads(line) for line in self.trace.read_text(encoding="utf-8").splitlines()
                      ] if self.trace.exists() else []
        self.invocations += 1
        if OPTIONS.evidence_dir:
            stem = Path(OPTIONS.evidence_dir) / f"{self._testMethodName}-{self.invocations}"
            stem.parent.mkdir(parents=True, exist_ok=True)
            stem.with_suffix(".log").write_text(self.output, encoding="utf-8")
            stem.with_suffix(".json").write_text(json.dumps({
                "command": invocation, "arguments": arguments, "exitCode": result.returncode,
                "dotnet": self.calls, "framework": framework, "failure": failure,
                "LC_ALL": self.env.get("LC_ALL"),
            }, indent=2, ensure_ascii=False), encoding="utf-8")
        self.assertEqual(result.returncode == 0, success, self.output)
        return result

    def runs(self):
        return [call[call.index("--test") + 1] for call in self.calls if call[0] == "run"]

    def builds(self):
        return [next(Path(arg).stem for arg in call[1:]
                     if Path(arg).stem == "Runner" or Path(arg).stem.startswith("Test"))
                for call in self.calls if call[0] == "build"]

    def summary(self, passed, failed=0, requested=0, windows=0):
        skipped = requested + (0 if self.managed else windows)
        expected = f"Total tests: {passed + failed + skipped}. Passed: {passed}. Failed: {failed}."
        self.assertIn(expected, self.output)
        if skipped or not self.managed:
            self.assertIn(f"Skipped: {skipped}.", self.output)

    def assert_not_built_or_run(self, *names):
        for name in names:
            self.assertNotIn(name, self.builds())
            self.assertNotIn(name, self.runs())

    def test_default_discovery_order_and_windows_distinction(self):
        self.invoke()
        self.assertEqual(self.runs(), self.available)
        self.summary(len(self.available), windows=0 if os.name == "nt" else len(WINDOWS))
        if os.name != "nt":
            self.assert_not_built_or_run(*WINDOWS)
            self.assertRegex(self.output, "Windows-only|windows-only")
            self.assertNotIn("skipped (requested)", self.output)

    def test_selection_order(self):
        self.invoke("TestZulu", "Test\u00dcnicode", "TestBeta", "TestAlpha")
        self.assertEqual(self.runs(), ["TestZulu", "Test\u00dcnicode", "TestBeta", "TestAlpha"])
        self.summary(4)

    def test_powershell_array_invocation(self):
        if OPTIONS.kind == "native-sh":
            self.skipTest("PowerShell invocation contract")
        self.invoke("TestAlpha", "TestBeta", "--skip", "TestBeta", powershell_array=True)
        self.assertEqual(self.runs(), ["TestAlpha"])
        self.assert_not_built_or_run("TestBeta")
        self.summary(1, requested=1)

    def test_selected_exclusion_never_builds_or_runs(self):
        self.invoke("TestAlpha", "TestBeta", "--skip", "TestBeta", failure="build:TestBeta")
        self.assertEqual(self.runs(), ["TestAlpha"])
        self.assert_not_built_or_run("TestBeta")
        self.assertIn("TestBeta ... skipped (requested)", self.output)
        self.summary(1, requested=1)

    def test_discovery_exclusions(self):
        self.invoke("--skip", "TestBeta", "TestServerCustomPort")
        self.assertEqual(self.runs(), [name for name in self.available
                                      if name not in ("TestBeta", "TestServerCustomPort")])
        self.assert_not_built_or_run("TestBeta", "TestServerCustomPort")
        self.summary(len(self.available) - 2, requested=2, windows=0 if os.name == "nt" else 2)

    def test_empty_skip_discovers_everything(self):
        self.invoke("--skip")
        self.assertEqual(self.runs(), self.available)
        self.summary(len(self.available), windows=0 if os.name == "nt" else 2)

    def test_repeated_skip_stays_in_exclusion_mode(self):
        self.invoke("TestAlpha", "TestBeta", "--skip", "TestBeta", "--skip", "TestAlpha", "TestBeta")
        self.assertEqual(self.runs(), [])
        self.assertEqual(self.builds(), ["Runner"])
        self.summary(0, requested=2)
        self.assertEqual(self.output.count("TestBeta ... skipped (requested)"), 1)

    def test_unknown_exclusions_are_ignored(self):
        self.invoke("TestAlpha", "--skip", "MissingTest", "")
        self.assertEqual(self.runs(), ["TestAlpha"])
        self.summary(1)

    def test_exclusion_names_are_case_sensitive(self):
        self.invoke("TestAlpha", "--skip", "testalpha")
        self.assertEqual(self.runs(), ["TestAlpha"])
        self.summary(1)

    def test_delimiter_is_case_sensitive(self):
        self.invoke("TestAlpha", "--SKIP", "TestBeta", success=False)
        self.assertNotIn("skipped (requested)", self.output)

    def test_all_discovered_projects_can_be_excluded(self):
        self.invoke("--skip", *(PORTABLE + WINDOWS))
        self.assertEqual(self.builds(), ["Runner"])
        self.assertEqual(self.runs(), [])
        self.summary(0, requested=len(PORTABLE + WINDOWS))
        for name in PORTABLE + WINDOWS:
            self.assertIn(name + " ... skipped (requested)", self.output)

    def test_requested_windows_skip_is_not_os_skip(self):
        self.invoke("TestWindowsForms", "--skip", "TestWindowsForms")
        self.assert_not_built_or_run("TestWindowsForms")
        self.assertIn("TestWindowsForms ... skipped (requested)", self.output)
        self.summary(0, requested=1)

    def test_framework_override_configuration_sources_and_server_routes(self):
        self.invoke("TestAlpha", "TestOmega_Release", "TestServerDefaultPort",
                    "TestServerCustomPort", "Test\u00dcnicode", framework="net11.0")
        self.summary(5)
        for call in (call for call in self.calls if call[0] == "run"):
            name = call[call.index("--test") + 1]
            config = "Release" if name.endswith("_Release") else "Debug"
            assembly = call[call.index("--assembly") + 1].replace("\\", "/")
            self.assertTrue(assembly.endswith(f"{name}/bin/{config}/net11.0/{name}.dll"), call)
            sources = call[call.index("--sources") + 1].replace("\\", "/")
            self.assertIn(f"{name}/Program.cs", sources)
            self.assertIn(f"{name}/Nested/Unicode \u00fc.cs", sources)
            if name in ("TestServerDefaultPort", "TestServerCustomPort"):
                self.assertEqual(call[call.index("--server") + 1],
                                 "4711" if name == "TestServerDefaultPort" else "22111")
            else:
                self.assertNotIn("--server", call)
        release_build = next(call for call in self.calls if call[0] == "build" and
                             any("TestOmega_Release" in arg for arg in call))
        self.assertEqual(release_build[release_build.index("-c") + 1], "Release")

    def test_default_framework(self):
        self.invoke("TestAlpha")
        run = next(call for call in self.calls if call[0] == "run")
        self.assertTrue(run[run.index("--assembly") + 1].replace("\\", "/")
                        .endswith("/net10.0/TestAlpha.dll"), run)

    def test_fixture_build_failure_continues_and_fails_suite(self):
        self.invoke("TestAlpha", "TestBeta", failure="build:TestAlpha", success=False)
        self.assertEqual(self.runs(), ["TestBeta"])
        self.assertIn("TestAlpha ... failed: build error", self.output)
        self.summary(1, failed=1)

    def test_fixture_runner_failure_continues_and_fails_suite(self):
        self.invoke("TestAlpha", "TestBeta", failure="run:TestAlpha", success=False)
        self.assertEqual(self.runs(), ["TestAlpha", "TestBeta"])
        self.summary(1, failed=1)

    def test_runner_build_failure_stops_before_fixtures(self):
        self.invoke("TestAlpha", failure="build:Runner", success=False)
        self.assertEqual(self.builds(), ["Runner"])
        self.assertEqual(self.runs(), [])

    def test_unknown_selection_fails(self):
        self.invoke("TestMissing", success=False)
        if self.managed:
            self.assertIn("Unknown test 'TestMissing'", self.output)
            self.assertEqual(self.calls, [])
        else:
            self.summary(0, failed=1)

    def test_no_projects(self):
        for project in self.suite.glob("Test*"):
            shutil.rmtree(project)
        self.invoke("--skip", "MissingTest")
        self.assertEqual(self.builds(), ["Runner"])
        self.assertEqual(self.runs(), [])
        self.summary(0)

    def managed_only(self):
        if not self.managed:
            self.skipTest("Existing managed-only UX, not a native contract")

    def test_managed_selection_remains_case_insensitive(self):
        self.managed_only()
        self.invoke("testalpha", "TESTBETA")
        self.assertEqual(self.runs(), ["TestAlpha", "TestBeta"])
        self.summary(2)
        self.invoke("testalpha", "TESTBETA", "--skip", "TestBeta")
        self.assertEqual(self.runs(), ["TestAlpha"])
        self.summary(1, requested=1)

    def test_managed_unknown_selection_is_not_hidden_by_exclusion(self):
        self.managed_only()
        self.invoke("TestMissing", "--skip", "TestMissing", success=False)
        self.assertIn("Unknown test 'TestMissing'", self.output)
        self.assertEqual(self.calls, [])

    def test_managed_explicit_windows_selection_rejected_on_posix(self):
        self.managed_only()
        if os.name == "nt":
            self.skipTest("POSIX-only validation")
        self.invoke("TestWindowsForms", success=False)
        self.assertIn("targets Windows", self.output)
        self.assertIn("cannot run", self.output)
        self.assertEqual(self.calls, [])

    def test_managed_matching_project_discovery_only(self):
        self.managed_only()
        (self.suite / "TestNotAProject").mkdir()
        (self.suite / "TestNotAProject/WrongName.csproj").touch()
        self.invoke()
        self.assertEqual(self.runs(), self.available)
        self.assert_not_built_or_run("TestNotAProject")

    def test_managed_obj_sources_are_excluded(self):
        self.managed_only()
        (self.suite / "TestAlpha/obj").mkdir()
        (self.suite / "TestAlpha/obj/Generated.cs").touch()
        self.invoke("TestAlpha")
        run = next(call for call in self.calls if call[0] == "run")
        self.assertNotIn("Generated.cs", run[run.index("--sources") + 1])
        self.assert_debugger_path(run[run.index("--local") + 1])
        self.assertIn("--no-build", run)

    def assert_debugger_path(self, actual):
        self.assertTrue(Path(actual).samefile(self.debugger), f"Wrong debugger file: {actual}")

    def test_managed_debugger_file_identity(self):
        self.managed_only()
        alias = self.debugger.with_name("debugger-alias" + self.debugger.suffix)
        os.link(self.debugger, alias)
        self.env["DNCDBG"] = str(alias)
        self.invoke("TestAlpha")
        run = next(call for call in self.calls if call[0] == "run")
        self.assert_debugger_path(run[run.index("--local") + 1])
        different = self.debugger.with_name("different-file" + self.debugger.suffix)
        different.touch()
        with self.assertRaises(AssertionError):
            self.assert_debugger_path(str(different))

    def test_managed_missing_debugger_fails_before_build(self):
        self.managed_only()
        self.debugger.unlink()
        self.invoke("TestAlpha", success=False)
        self.assertIn("Debugger apphost not found", self.output)
        self.assertEqual(self.calls, [])

    def test_managed_windows_framework_suffix_is_not_duplicated(self):
        self.managed_only()
        if os.name != "nt":
            self.skipTest("Requires an actual Windows host; OS detection is not mocked")
        for framework in ("net11.0", "net11.0-windows"):
            self.invoke("TestWindowsForms", framework=framework)
            run = next(call for call in self.calls if call[0] == "run")
            self.assertTrue(run[run.index("--assembly") + 1].replace("\\", "/")
                            .endswith("/net11.0-windows/TestWindowsForms.dll"), run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--script", default=str(ROOT / "test-suite/run_tests.ps1"))
    parser.add_argument("--kind", choices=("managed", "native-ps1", "native-sh"), default="managed")
    parser.add_argument("--evidence-dir")
    OPTIONS, tests = parser.parse_known_args()
    OPTIONS.script = str(Path(OPTIONS.script).resolve())
    if OPTIONS.kind != "native-sh":
        environment = os.environ.copy()
        environment["RUNNER_TEST_NAMES"] = json.dumps(PORTABLE + WINDOWS)
        host = json.loads(subprocess.check_output([
            "pwsh", "-NoLogo", "-NoProfile", "-Command",
            "$names = [string[]]@(ConvertFrom-Json $env:RUNNER_TEST_NAMES); "
            "[array]::Sort($names, [StringComparer]::CurrentCultureIgnoreCase); "
            "@{ Host = [Environment]::ProcessPath; Home = $PSHOME; "
            "Culture = [cultureinfo]::CurrentCulture.Name; ProjectOrder = $names } | ConvertTo-Json -Compress",
        ], env=environment, text=True, encoding="utf-8", timeout=30))
        OPTIONS.project_order = host["ProjectOrder"]
        if OPTIONS.evidence_dir:
            evidence = Path(OPTIONS.evidence_dir)
            evidence.mkdir(parents=True, exist_ok=True)
            (evidence / "powershell-host.json").write_text(
                json.dumps(host, indent=2, ensure_ascii=False), encoding="utf-8")
        OPTIONS.pwsh = [host["Host"]]
        if Path(host["Host"]).stem == "dotnet":
            # A dotnet-tool shim must not resolve its own host through the stub PATH.
            OPTIONS.pwsh.append(str(Path(host["Home"]) / "pwsh.dll"))
    unittest.main(argv=[sys.argv[0]] + tests)
