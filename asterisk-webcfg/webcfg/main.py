from __future__ import annotations

from pathlib import Path
from typing import Annotated

from fastapi import Depends, FastAPI, Form, HTTPException, Request
from fastapi.responses import HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates
import re

from .asterisk import reload_dialplan, reload_pjsip
from .auth import require_admin
from .config import Settings, get_settings
from .fsops import atomic_write_text, ensure_dir, safe_unlink
from .pjsip_templates import PjsipUser, render_user_conf


app = FastAPI(title="asterisk-webcfg", version="0.1.0")
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))


def _paths(settings: Settings) -> dict[str, Path]:
    etc = Path(settings.asterisk_etc)
    return {
        "etc": etc,
        "pjsip_users_dir": etc / "pjsip.d" / "users",
        "ext_dir": etc / "extensions.d",
        "backup_dir": Path(settings.backup_dir),
    }


def _list_conf_files(dir_path: Path) -> list[Path]:
    if not dir_path.exists():
        return []
    return sorted([p for p in dir_path.glob("*.conf") if p.is_file()], key=lambda p: p.name.lower())

_PASSWORD_RE = re.compile(r"(?m)^\s*password\s*=\s*(.+?)\s*$")


def _extract_existing_password(conf_text: str) -> str | None:
    m = _PASSWORD_RE.search(conf_text)
    return m.group(1) if m else None


@app.get("/", response_class=HTMLResponse)
def home(
    request: Request,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    p = _paths(settings)
    return templates.TemplateResponse(
        request,
        "home.html",
        {
            "pjsip_users_dir": str(p["pjsip_users_dir"]),
            "ext_dir": str(p["ext_dir"]),
            "container": settings.asterisk_container,
        },
    )


# -------------------- Users --------------------


@app.get("/users", response_class=HTMLResponse)
def users_list(
    request: Request,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    p = _paths(settings)
    files = _list_conf_files(p["pjsip_users_dir"])
    users = [{"extension": f.stem, "path": str(f)} for f in files]
    return templates.TemplateResponse(request, "users_list.html", {"users": users})


@app.get("/users/new", response_class=HTMLResponse)
def users_new_form(request: Request, _: Annotated[str, Depends(require_admin)]):
    return templates.TemplateResponse(request, "users_edit.html", {"user": None, "error": None})


@app.get("/users/{extension}", response_class=HTMLResponse)
def users_edit_form(
    request: Request,
    extension: str,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    p = _paths(settings)
    conf = p["pjsip_users_dir"] / f"{extension}.conf"
    if not conf.exists():
        raise HTTPException(status_code=404, detail="Not found")
    raw = conf.read_text(encoding="utf-8")
    return templates.TemplateResponse(
        request,
        "users_edit.html",
        {
            "user": {"extension": extension, "raw": raw},
            "error": None,
        },
    )


@app.post("/users/save")
def users_save(
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
    extension: str = Form(...),
    password: str = Form(""),
    transport: str = Form("transport-tls"),
    context: str = Form("from-internal"),
    allow: str = Form("opus,alaw,ulaw,g722"),
    max_contacts: int = Form(2),
    media_encryption: str = Form("sdes"),
):
    ext = extension.strip()
    if not ext.isdigit():
        raise HTTPException(status_code=400, detail="extension must be digits")

    p = _paths(settings)
    ensure_dir(p["pjsip_users_dir"])
    conf_path = p["pjsip_users_dir"] / f"{ext}.conf"
    pwd = password.strip()
    if pwd == "":
        if conf_path.exists():
            existing = conf_path.read_text(encoding="utf-8")
            extracted = _extract_existing_password(existing)
            if extracted is None:
                raise HTTPException(status_code=400, detail="password required (could not read existing)")
            pwd = extracted
        else:
            raise HTTPException(status_code=400, detail="password required")

    user = PjsipUser(
        extension=ext,
        password=pwd,
        transport=transport,
        context=context,
        allow=allow,
        max_contacts=max_contacts,
        media_encryption=media_encryption,
    )
    content = render_user_conf(user)
    atomic_write_text(
        path=conf_path,
        content=content,
        backup_dir=p["backup_dir"] / "pjsip-users",
    )
    return RedirectResponse(url="/users", status_code=303)


@app.post("/users/{extension}/delete")
def users_delete(
    extension: str,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    p = _paths(settings)
    conf = p["pjsip_users_dir"] / f"{extension}.conf"
    safe_unlink(path=conf, backup_dir=p["backup_dir"] / "pjsip-users")
    return RedirectResponse(url="/users", status_code=303)


@app.post("/apply/pjsip", response_class=HTMLResponse)
def apply_pjsip(
    request: Request,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    res = reload_pjsip(container=settings.asterisk_container)
    return templates.TemplateResponse(request, "apply_result.html", {"title": "PJSIP reload", "res": res})


# -------------------- Dialplan fragments --------------------


@app.get("/dialplan", response_class=HTMLResponse)
def dialplan_list(
    request: Request,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    p = _paths(settings)
    files = _list_conf_files(p["ext_dir"])
    frags = [{"name": f.name, "path": str(f)} for f in files]
    return templates.TemplateResponse(request, "dialplan_list.html", {"frags": frags})


@app.get("/dialplan/new", response_class=HTMLResponse)
def dialplan_new_form(request: Request, _: Annotated[str, Depends(require_admin)]):
    return templates.TemplateResponse(request, "dialplan_edit.html", {"frag": None, "error": None})


@app.get("/dialplan/{name}", response_class=HTMLResponse)
def dialplan_edit_form(
    request: Request,
    name: str,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    if "/" in name or "\\" in name:
        raise HTTPException(status_code=400, detail="invalid name")
    p = _paths(settings)
    conf = p["ext_dir"] / name
    if not conf.exists():
        raise HTTPException(status_code=404, detail="Not found")
    raw = conf.read_text(encoding="utf-8")
    return templates.TemplateResponse(request, "dialplan_edit.html", {"frag": {"name": name, "raw": raw}, "error": None})


@app.post("/dialplan/save")
def dialplan_save(
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
    name: str = Form(...),
    raw: str = Form(""),
):
    file_name = name.strip()
    if file_name == "" or "/" in file_name or "\\" in file_name:
        raise HTTPException(status_code=400, detail="invalid name")
    if not file_name.endswith(".conf"):
        file_name += ".conf"

    p = _paths(settings)
    ensure_dir(p["ext_dir"])
    atomic_write_text(
        path=p["ext_dir"] / file_name,
        content=raw.replace("\r\n", "\n"),
        backup_dir=p["backup_dir"] / "dialplan",
    )
    return RedirectResponse(url="/dialplan", status_code=303)


@app.post("/dialplan/{name}/delete")
def dialplan_delete(
    name: str,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    if "/" in name or "\\" in name:
        raise HTTPException(status_code=400, detail="invalid name")
    p = _paths(settings)
    conf = p["ext_dir"] / name
    safe_unlink(path=conf, backup_dir=p["backup_dir"] / "dialplan")
    return RedirectResponse(url="/dialplan", status_code=303)


@app.post("/apply/dialplan", response_class=HTMLResponse)
def apply_dialplan(
    request: Request,
    _: Annotated[str, Depends(require_admin)],
    settings: Annotated[Settings, Depends(get_settings)],
):
    res = reload_dialplan(container=settings.asterisk_container)
    return templates.TemplateResponse(request, "apply_result.html", {"title": "Dialplan reload", "res": res})

