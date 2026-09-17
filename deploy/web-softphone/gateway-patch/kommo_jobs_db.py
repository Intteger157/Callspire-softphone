"""SQLite storage for Kommo process-call jobs."""

from __future__ import annotations

import json
import sqlite3
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Optional


def _parse_call_time_iso(value: str) -> Optional[datetime]:
    if not value:
        return None
    try:
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)
    except ValueError:
        return None

_DB_PATH: Optional[Path] = None


def init_db(db_path: str | Path | None = None) -> None:
    global _DB_PATH
    if db_path is not None:
        _DB_PATH = Path(db_path)
    elif _DB_PATH is None:
        _DB_PATH = Path(__file__).resolve().parent / "kommo_jobs.sqlite"
    _DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    with _connect() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS kommo_call_jobs (
                id TEXT PRIMARY KEY,
                extension TEXT NOT NULL,
                dedup_key TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'queued',
                lead_id INTEGER,
                upload_source TEXT,
                reason TEXT,
                recording_path TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_kommo_call_jobs_status ON kommo_call_jobs(status)"
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_kommo_call_jobs_extension ON kommo_call_jobs(extension)"
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS kommo_pbx_linkedid_claims (
                linkedid TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                extension TEXT NOT NULL,
                phone TEXT,
                session_id TEXT,
                call_time TEXT,
                created_at TEXT NOT NULL
            )
            """
        )
        conn.execute(
            """
            CREATE INDEX IF NOT EXISTS idx_kommo_linkedid_claims_ext
            ON kommo_pbx_linkedid_claims(extension, created_at)
            """
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS kommo_recording_hashes (
                sha256 TEXT NOT NULL,
                extension TEXT NOT NULL,
                phone TEXT,
                lead_id INTEGER,
                job_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (sha256, extension)
            )
            """
        )
        conn.commit()


def _connect() -> sqlite3.Connection:
    if _DB_PATH is None:
        init_db()
    conn = sqlite3.connect(str(_DB_PATH))
    conn.row_factory = sqlite3.Row
    return conn


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def create_job(
    extension: str,
    dedup_key: str,
    payload: dict[str, Any],
    *,
    status: str = "queued",
) -> dict[str, Any]:
    job_id = str(uuid.uuid4())
    now = _now_iso()
    with _connect() as conn:
        try:
            conn.execute(
                """
                INSERT INTO kommo_call_jobs
                    (id, extension, dedup_key, payload_json, status, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (job_id, extension, dedup_key, json.dumps(payload), status, now, now),
            )
            conn.commit()
        except sqlite3.IntegrityError:
            row = conn.execute(
                "SELECT * FROM kommo_call_jobs WHERE dedup_key = ?", (dedup_key,)
            ).fetchone()
            if row:
                return _row_to_dict(row)
            raise
    return get_job(job_id) or {}


def get_job(job_id: str) -> Optional[dict[str, Any]]:
    with _connect() as conn:
        row = conn.execute(
            "SELECT * FROM kommo_call_jobs WHERE id = ?", (job_id,)
        ).fetchone()
    return _row_to_dict(row) if row else None


def get_job_by_dedup(dedup_key: str) -> Optional[dict[str, Any]]:
    with _connect() as conn:
        row = conn.execute(
            "SELECT * FROM kommo_call_jobs WHERE dedup_key = ?", (dedup_key,)
        ).fetchone()
    return _row_to_dict(row) if row else None


def update_job(job_id: str, **fields: Any) -> Optional[dict[str, Any]]:
    allowed = {
        "status",
        "lead_id",
        "upload_source",
        "reason",
        "recording_path",
        "payload_json",
    }
    parts: list[str] = []
    values: list[Any] = []
    for key, value in fields.items():
        if key not in allowed:
            continue
        parts.append(f"{key} = ?")
        if key == "payload_json" and isinstance(value, dict):
            values.append(json.dumps(value))
        else:
            values.append(value)
    if not parts:
        return get_job(job_id)
    parts.append("updated_at = ?")
    values.append(_now_iso())
    values.append(job_id)
    with _connect() as conn:
        conn.execute(
            f"UPDATE kommo_call_jobs SET {', '.join(parts)} WHERE id = ?",
            values,
        )
        conn.commit()
    return get_job(job_id)


def list_jobs_by_status(statuses: list[str], limit: int = 50) -> list[dict[str, Any]]:
    placeholders = ",".join("?" for _ in statuses)
    with _connect() as conn:
        rows = conn.execute(
            f"""
            SELECT * FROM kommo_call_jobs
            WHERE status IN ({placeholders})
            ORDER BY created_at ASC
            LIMIT ?
            """,
            (*statuses, limit),
        ).fetchall()
    return [_row_to_dict(r) for r in rows]


def claim_next_job(statuses: list[str]) -> Optional[dict[str, Any]]:
    """Atomically pick one ready job (oldest first) and mark it processing."""
    placeholders = ",".join("?" for _ in statuses)
    now = _now_iso()
    with _connect() as conn:
        row = conn.execute(
            f"""
            SELECT id FROM kommo_call_jobs
            WHERE status IN ({placeholders})
            AND (
                json_extract(payload_json, '$.retry_after') IS NULL
                OR json_extract(payload_json, '$.retry_after') <= ?
            )
            ORDER BY created_at ASC
            LIMIT 1
            """,
            (*statuses, now),
        ).fetchone()
        if not row:
            return None
        cur = conn.execute(
            f"""
            UPDATE kommo_call_jobs
            SET status = 'processing', updated_at = ?
            WHERE id = ? AND status IN ({placeholders})
            """,
            (now, row["id"], *statuses),
        )
        conn.commit()
        if cur.rowcount != 1:
            return None
        claimed = conn.execute(
            "SELECT * FROM kommo_call_jobs WHERE id = ?", (row["id"],)
        ).fetchone()
    return _row_to_dict(claimed) if claimed else None


def recover_orphaned_processing_jobs() -> int:
    """Requeue jobs left in processing after a service restart killed workers."""
    now = _now_iso()
    with _connect() as conn:
        cur = conn.execute(
            """
            UPDATE kommo_call_jobs
            SET status = 'queued',
                reason = 'Requeued after service restart',
                updated_at = ?
            WHERE status = 'processing'
            """,
            (now,),
        )
        conn.commit()
        return cur.rowcount


def list_uploaded_jobs_for_extension(
    extension: str,
    *,
    hours: int = 72,
    limit: int = 100,
) -> list[dict[str, Any]]:
    cutoff = (datetime.now(timezone.utc) - timedelta(hours=max(1, hours))).isoformat()
    with _connect() as conn:
        rows = conn.execute(
            """
            SELECT * FROM kommo_call_jobs
            WHERE extension = ? AND status = 'uploaded' AND created_at >= ?
            ORDER BY created_at DESC
            LIMIT ?
            """,
            (extension, cutoff, limit),
        ).fetchall()
    items = []
    for row in rows:
        job = _row_to_dict(row)
        payload = job.get("payload") or {}
        items.append(
            {
                "id": job["id"],
                "phone": payload.get("phone"),
                "call_time": payload.get("call_time"),
                "lead_id": job.get("lead_id"),
                "status": job["status"],
                "upload_source": job.get("upload_source"),
                "reason": job.get("reason"),
                "created_at": job.get("created_at"),
            }
        )
    return items


def find_overlapping_active_job(
    extension: str,
    phone: str,
    call_time: str,
    *,
    window_sec: int = 60,
    except_job_id: Optional[str] = None,
) -> Optional[dict[str, Any]]:
    """Find an in-flight or uploaded job for the same call (web + desktop double submit)."""
    phone = (phone or "").strip()
    call_dt = _parse_call_time_iso(call_time)
    if not phone or call_dt is None:
        return None
    cutoff = (call_dt - timedelta(seconds=max(30, window_sec * 2))).isoformat()
    with _connect() as conn:
        rows = conn.execute(
            """
            SELECT * FROM kommo_call_jobs
            WHERE extension = ?
              AND created_at >= ?
              AND status IN ('queued', 'waiting_recording', 'processing', 'uploaded')
            ORDER BY created_at ASC
            """,
            (extension, cutoff),
        ).fetchall()
    for row in rows:
        if except_job_id and row["id"] == except_job_id:
            continue
        job = _row_to_dict(row)
        payload = job.get("payload") or {}
        if (payload.get("phone") or "").strip() != phone:
            continue
        other_dt = _parse_call_time_iso(str(payload.get("call_time") or ""))
        if other_dt is None:
            continue
        if abs((other_dt - call_dt).total_seconds()) <= window_sec:
            return job
    return None


def is_recording_hash_used(
    extension: str,
    sha256: str,
    *,
    hours: int = 72,
    exclude_job_id: Optional[str] = None,
) -> bool:
    digest = (sha256 or "").strip().lower()
    if not digest:
        return False
    cutoff = (datetime.now(timezone.utc) - timedelta(hours=max(1, hours))).isoformat()
    with _connect() as conn:
        row = conn.execute(
            """
            SELECT job_id FROM kommo_recording_hashes
            WHERE extension = ? AND sha256 = ? AND created_at >= ?
            """,
            (extension, digest, cutoff),
        ).fetchone()
    if not row:
        return False
    if exclude_job_id and row["job_id"] == exclude_job_id:
        return False
    return True


def register_recording_hash(
    extension: str,
    sha256: str,
    job_id: str,
    *,
    phone: Optional[str] = None,
    lead_id: Optional[int] = None,
) -> None:
    digest = (sha256 or "").strip().lower()
    if not digest:
        return
    now = _now_iso()
    with _connect() as conn:
        conn.execute(
            """
            INSERT OR REPLACE INTO kommo_recording_hashes
                (sha256, extension, phone, lead_id, job_id, created_at)
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            (digest, extension, phone, lead_id, job_id, now),
        )
        conn.commit()


def try_claim_recording_hash(
    extension: str,
    sha256: str,
    job_id: str,
    *,
    phone: Optional[str] = None,
    lead_id: Optional[int] = None,
) -> bool:
    """Reserve audio hash before Kommo upload (prevents parallel duplicate uploads)."""
    digest = (sha256 or "").strip().lower()
    if not digest:
        return False
    now = _now_iso()
    with _connect() as conn:
        try:
            conn.execute(
                """
                INSERT INTO kommo_recording_hashes
                    (sha256, extension, phone, lead_id, job_id, created_at)
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (digest, extension, phone, lead_id, job_id, now),
            )
            conn.commit()
            return True
        except sqlite3.IntegrityError:
            row = conn.execute(
                """
                SELECT job_id FROM kommo_recording_hashes
                WHERE sha256 = ? AND extension = ?
                """,
                (digest, extension),
            ).fetchone()
            return bool(row and row["job_id"] == job_id)


def fail_superseded_queued_jobs(
    extension: str,
    phone: str,
    *,
    except_job_id: str,
) -> int:
    """Drop older queued jobs for the same call target when a newer call is submitted."""
    phone = (phone or "").strip()
    if not phone:
        return 0
    now = _now_iso()
    count = 0
    with _connect() as conn:
        rows = conn.execute(
            """
            SELECT id, payload_json FROM kommo_call_jobs
            WHERE extension = ?
              AND status IN ('queued', 'waiting_recording')
              AND id != ?
            """,
            (extension, except_job_id),
        ).fetchall()
        for row in rows:
            try:
                payload = json.loads(row["payload_json"] or "{}")
            except json.JSONDecodeError:
                continue
            if (payload.get("phone") or "").strip() != phone:
                continue
            conn.execute(
                """
                UPDATE kommo_call_jobs
                SET status = 'failed',
                    reason = 'Superseded by newer call',
                    updated_at = ?
                WHERE id = ?
                """,
                (now, row["id"]),
            )
            count += 1
        conn.commit()
    return count


def _linkedid_from_job_dict(job: dict[str, Any]) -> Optional[str]:
    payload = job.get("payload") or {}
    lid = payload.get("pbx_linkedid")
    if lid:
        return str(lid).strip() or None
    for path in (job.get("recording_path"), payload.get("recording_path")):
        if not path:
            continue
        name = Path(str(path)).stem
        if name.startswith("mikopbx_"):
            return name[len("mikopbx_") :] or None
    return None


def try_claim_pbx_linkedid(
    linkedid: str,
    job_id: str,
    extension: str,
    *,
    phone: Optional[str] = None,
    session_id: Optional[str] = None,
    call_time: Optional[str] = None,
) -> bool:
    """Atomically reserve a PBX linkedid for one Kommo job (prevents duplicate recordings)."""
    lid = (linkedid or "").strip()
    if not lid:
        return False
    now = _now_iso()
    with _connect() as conn:
        try:
            conn.execute(
                """
                INSERT INTO kommo_pbx_linkedid_claims
                    (linkedid, job_id, extension, phone, session_id, call_time, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (lid, job_id, extension, phone, session_id, call_time, now),
            )
            conn.commit()
            return True
        except sqlite3.IntegrityError:
            return False


def release_pbx_linkedid_claim(linkedid: str, job_id: str) -> bool:
    """Release a claim when the matched recording was rejected (wrong CDR leg)."""
    lid = (linkedid or "").strip()
    if not lid:
        return False
    with _connect() as conn:
        cur = conn.execute(
            """
            DELETE FROM kommo_pbx_linkedid_claims
            WHERE linkedid = ? AND job_id = ?
            """,
            (lid, job_id),
        )
        conn.commit()
        return cur.rowcount > 0


def get_pbx_linkedid_owner(linkedid: str) -> Optional[dict[str, Any]]:
    lid = (linkedid or "").strip()
    if not lid:
        return None
    with _connect() as conn:
        row = conn.execute(
            "SELECT * FROM kommo_pbx_linkedid_claims WHERE linkedid = ?",
            (lid,),
        ).fetchone()
    if not row:
        return None
    return {
        "linkedid": row["linkedid"],
        "job_id": row["job_id"],
        "extension": row["extension"],
        "phone": row["phone"],
        "session_id": row["session_id"],
        "call_time": row["call_time"],
        "created_at": row["created_at"],
    }


def list_used_pbx_linkedids(
    extension: str,
    *,
    hours: int = 48,
    exclude_job_id: Optional[str] = None,
) -> set[str]:
    """PBX linkedids already claimed or uploaded — must not be reused for another call."""
    cutoff = (datetime.now(timezone.utc) - timedelta(hours=max(1, hours))).isoformat()
    used: set[str] = set()
    with _connect() as conn:
        claim_rows = conn.execute(
            """
            SELECT linkedid, job_id FROM kommo_pbx_linkedid_claims
            WHERE extension = ? AND created_at >= ?
            """,
            (extension, cutoff),
        ).fetchall()
        job_rows = conn.execute(
            """
            SELECT id, payload_json, recording_path, status FROM kommo_call_jobs
            WHERE extension = ?
              AND created_at >= ?
              AND status IN ('uploaded', 'processing', 'queued', 'waiting_recording')
            """,
            (extension, cutoff),
        ).fetchall()
    for row in claim_rows:
        if exclude_job_id and row["job_id"] == exclude_job_id:
            continue
        if row["linkedid"]:
            used.add(str(row["linkedid"]))
    for row in job_rows:
        if exclude_job_id and row["id"] == exclude_job_id:
            continue
        job = {
            "payload": json.loads(row["payload_json"] or "{}"),
            "recording_path": row["recording_path"],
        }
        lid = _linkedid_from_job_dict(job)
        if lid:
            used.add(lid)
    return used


def cleanup_old_jobs(days: int = 7) -> int:
    cutoff = (datetime.now(timezone.utc) - timedelta(days=days)).isoformat()
    with _connect() as conn:
        conn.execute(
            "DELETE FROM kommo_pbx_linkedid_claims WHERE created_at < ?",
            (cutoff,),
        )
        conn.execute(
            "DELETE FROM kommo_recording_hashes WHERE created_at < ?",
            (cutoff,),
        )
        cur = conn.execute(
            "DELETE FROM kommo_call_jobs WHERE created_at < ? AND status IN ('uploaded', 'failed', 'skipped')",
            (cutoff,),
        )
        conn.commit()
        return cur.rowcount


def _row_to_dict(row: sqlite3.Row) -> dict[str, Any]:
    payload = json.loads(row["payload_json"] or "{}")
    return {
        "id": row["id"],
        "extension": row["extension"],
        "dedup_key": row["dedup_key"],
        "payload": payload,
        "status": row["status"],
        "lead_id": row["lead_id"],
        "upload_source": row["upload_source"],
        "reason": row["reason"],
        "recording_path": row["recording_path"],
        "created_at": row["created_at"],
        "updated_at": row["updated_at"],
    }
