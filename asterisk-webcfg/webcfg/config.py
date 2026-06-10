from __future__ import annotations

import os
from dataclasses import dataclass


def _get_env(name: str, default: str) -> str:
    value = os.getenv(name)
    return value if value is not None and value != "" else default


@dataclass(frozen=True)
class Settings:
    asterisk_etc: str
    asterisk_container: str
    backup_dir: str
    admin_user: str
    admin_pass: str


def get_settings() -> Settings:
    return Settings(
        asterisk_etc=_get_env("ASTERISK_ETC", "/etc/asterisk"),
        asterisk_container=_get_env("ASTERISK_CONTAINER", "asterisk"),
        backup_dir=_get_env("BACKUP_DIR", "/var/backups/asterisk-webcfg"),
        admin_user=_get_env("ADMIN_USER", "admin"),
        admin_pass=_get_env("ADMIN_PASS", "admin"),
    )

