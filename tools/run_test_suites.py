#!/usr/bin/env python3
"""Run the Python test suites and refuse one that discovered nothing.

`python -m unittest discover` prints "Ran 0 tests ... OK" and exits 0 when its pattern matches no
file, so a renamed or relocated test module takes its entire suite out of CI without turning
anything red. That is the failure this repository least wants: the checks stay green while the
thing they check stops being checked. Both stacks behave this way — `dotnet test` also exits 0 on
a project with no tests, which is why the workflow asserts a passing count for the .NET suites
rather than trusting their exit codes either.

Run with no arguments to run every suite as a child process, which is how CI invokes it and keeps
the suites as isolated from each other as separate `unittest` runs were. Run with one directory to
run that suite in this process; it exits 2 when the suite is empty, 1 when a test fails.

Standard library only, like every other tool here: this runs before anything is installed.
"""

from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

SUITES = (
    "src/document-processing",
    "src/functions",
    "tools",
)

EMPTY_SUITE = 2


def run_suite(start_dir: Path) -> int:
    suite = unittest.TestLoader().discover(
        start_dir=str(start_dir), pattern="test_*.py", top_level_dir=str(start_dir)
    )
    discovered = suite.countTestCases()
    if discovered == 0:
        print(
            f"ERROR: {start_dir} discovered no tests. A suite that matches no file still exits 0, "
            "so this is a rename or a move that silently removed it from CI rather than an empty "
            "directory anybody intended.",
            file=sys.stderr,
        )
        return EMPTY_SUITE

    result = unittest.TextTestRunner(verbosity=1).run(suite)
    return 0 if result.wasSuccessful() else 1


def main(argv: list[str]) -> int:
    if len(argv) > 1:
        return run_suite(Path(argv[1]))

    failures = 0
    for suite in SUITES:
        print(f"== {suite}", flush=True)
        completed = subprocess.run([sys.executable, __file__, str(ROOT / suite)], cwd=ROOT)
        if completed.returncode != 0:
            failures += 1
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
