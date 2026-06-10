from __future__ import annotations

import secrets
from typing import Annotated

from fastapi import Depends, HTTPException
from fastapi.security import HTTPBasic, HTTPBasicCredentials

from .config import Settings, get_settings


security = HTTPBasic()


def require_admin(
    credentials: Annotated[HTTPBasicCredentials, Depends(security)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> str:
    ok_user = secrets.compare_digest(credentials.username, settings.admin_user)
    ok_pass = secrets.compare_digest(credentials.password, settings.admin_pass)
    if not (ok_user and ok_pass):
        raise HTTPException(status_code=401, detail="Unauthorized", headers={"WWW-Authenticate": "Basic"})
    return credentials.username

