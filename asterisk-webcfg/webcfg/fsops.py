from __future__ import annotations

import os
import shutil
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True)
class WriteResult:
    path: Path
    backup_path: Path | None


def ensure_dir(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)


def _utc_stamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def atomic_write_text(
    *,
    path: Path,
    content: str,
    backup_dir: Path,
    mode: int = 0o640,
) -> WriteResult:
    ensure_dir(path.parent)
    ensure_dir(backup_dir)

    backup_path: Path | None = None
    if path.exists():
        backup_name = f"{path.name}.{_utc_stamp()}.bak"
        backup_path = backup_dir / backup_name
        shutil.copy2(path, backup_path)

    tmp_fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=str(path.parent))
    tmp_path = Path(tmp_name)
    try:
        with os.fdopen(tmp_fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
            f.flush()
            os.fsync(f.fileno())

        os.chmod(tmp_path, mode)
        os.replace(tmp_path, path)
    finally:
        try:
            if tmp_path.exists():
                tmp_path.unlink()
        except Exception:
            # best effort cleanup
            pass

    return WriteResult(path=path, backup_path=backup_path)


def safe_unlink(*, path: Path, backup_dir: Path) -> Path | None:
    if not path.exists():
        return None
    ensure_dir(backup_dir)
    backup_name = f"{path.name}.{_utc_stamp()}.deleted"
    backup_path = backup_dir / backup_name
    shutil.move(str(path), str(backup_path))
    return backup_path

