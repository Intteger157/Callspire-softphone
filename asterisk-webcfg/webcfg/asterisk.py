from __future__ import annotations

import subprocess
from dataclasses import dataclass


@dataclass(frozen=True)
class ExecResult:
    ok: bool
    command: list[str]
    stdout: str
    stderr: str
    exit_code: int


def docker_exec_asterisk_rx(*, container: str, rx: str, timeout_s: int = 20) -> ExecResult:
    cmd = ["docker", "exec", container, "asterisk", "-rx", rx]
    try:
        cp = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=timeout_s,
            check=False,
        )
        return ExecResult(
            ok=cp.returncode == 0,
            command=cmd,
            stdout=cp.stdout,
            stderr=cp.stderr,
            exit_code=cp.returncode,
        )
    except subprocess.TimeoutExpired as e:
        return ExecResult(
            ok=False,
            command=cmd,
            stdout=(e.stdout or ""),
            stderr=(e.stderr or "timeout"),
            exit_code=124,
        )


def reload_pjsip(*, container: str) -> ExecResult:
    return docker_exec_asterisk_rx(container=container, rx="pjsip reload")


def reload_dialplan(*, container: str) -> ExecResult:
    return docker_exec_asterisk_rx(container=container, rx="dialplan reload")

