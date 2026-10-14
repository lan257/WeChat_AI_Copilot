from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import importlib
import http.client
import ipaddress
import json
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any, Iterator


READER_ROOT = Path(__file__).resolve().parent
RUNTIME_ROOT = READER_ROOT / ".runtime"
PROVIDER = "local-wechat"
MIN_SOURCE_EPOCH = 946684800
MAX_SOURCE_EPOCH = 4102444800
MAX_HISTORY_ROWS = 5000
MAX_BATCH_SIZE = 500
MAX_MONITORED_TARGETS = 20
MAX_DISCOVERY_SESSIONS = 2000
# A single keyset page per shard. This bounds one unit of work; it is no longer the size of the
# requested window, so a busy three-month range continues from the last local_id instead of
# failing the whole job.
HISTORY_SCAN_PAGE_ROWS = 500
# Absolute runaway guard for one job. Reaching it means the source is not behaving like a bounded
# conversation window, which is a bug to surface rather than an amount to keep scanning.
HISTORY_JOB_ROW_GUARD = 50_000
MAX_API_RESPONSE_BYTES = 65536
TEXT_LOCAL_TYPE = 1
TABLE_NAME_RE = re.compile(r"^Msg_[0-9a-fA-F]{32}$")
FORBIDDEN_MODULES = {"wechatauto.wx", "wechatauto.guia", "wechatauto.cli", "wechatauto.moment"}
EXCLUDED_DIRECT_IDS = {
    "filehelper",
    "fmessage",
    "newsapp",
    "weixin",
    "tmessage",
    "brandsessionholder",
    "notifymessage",
}
MESSAGE_TYPES = {
    "文本": "Text",
    "图片": "Image",
    "语音": "Voice",
    "视频": "Video",
    "动画表情": "Emoji",
    "位置": "Location",
    "文件/链接/卡片": "Other",
    "系统消息": "System",
    "音视频通话": "Other",
}


class ReaderFailure(Exception):
    def __init__(self, code: str, details: dict[str, Any] | None = None):
        super().__init__(code)
        self.code = code
        self.details = details or {}


def _error(code: str, details: dict[str, Any] | None = None) -> None:
    destination = sys.__stderr__ or sys.stderr
    error = {"code": code}
    if details:
        error.update(details)
    print(json.dumps({"error": error}, ensure_ascii=False, separators=(",", ":")), file=destination)


def _validate_api_base_url(value: Any) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > 2048:
        raise ReaderFailure("READER_API_BASE_URL_REQUIRED")
    base_url = value.strip().rstrip("/")
    if any(character.isspace() or ord(character) < 33 or ord(character) > 126 for character in base_url):
        raise ReaderFailure("READER_API_BASE_URL_INVALID")
    try:
        parsed = urllib.parse.urlsplit(base_url)
        hostname = parsed.hostname
        port = parsed.port
    except ValueError:
        raise ReaderFailure("READER_API_BASE_URL_INVALID") from None
    if (parsed.scheme not in {"http", "https"} or not hostname or parsed.username or parsed.password
            or parsed.query or parsed.fragment):
        raise ReaderFailure("READER_API_BASE_URL_INVALID")
    if port is not None and not 1 <= port <= 65535:
        raise ReaderFailure("READER_API_BASE_URL_INVALID")
    if parsed.scheme == "http":
        try:
            is_loopback = ipaddress.ip_address(hostname).is_loopback
        except ValueError:
            is_loopback = hostname.casefold() == "localhost"
        if not is_loopback:
            raise ReaderFailure("HTTPS_REQUIRED_FOR_REMOTE_READER_API")
    return base_url


def _validate_transport(value: Any) -> dict[str, Any]:
    if value is None:
        value = {}
    if not isinstance(value, dict):
        raise ReaderFailure("READER_TRANSPORT_SETTINGS_INVALID")
    enabled = value.get("enabled", False)
    if not isinstance(enabled, bool):
        raise ReaderFailure("READER_TRANSPORT_SETTINGS_INVALID")
    if not enabled:
        return {"enabled": False, "apiBaseUrl": "", "readerBearerToken": ""}

    base_url = _validate_api_base_url(value.get("apiBaseUrl"))
    token = value.get("readerBearerToken")
    if not isinstance(token, str) or len(token) < 32 or len(token) > 8192 or not token.isascii():
        raise ReaderFailure("READER_BEARER_TOKEN_INVALID")
    if (token != token.strip()
            or any(character.isspace() or ord(character) < 33 or ord(character) > 126 for character in token)):
        raise ReaderFailure("READER_BEARER_TOKEN_INVALID")
    return {"enabled": True, "apiBaseUrl": base_url, "readerBearerToken": token}


def _load_settings() -> dict[str, Any]:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise ReaderFailure("WINDOWS_REQUIRED")
    config_path = Path(local_app_data) / "WeChatAiCopilot" / "Reader" / "settings.json"
    try:
        parsed = json.loads(config_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise ReaderFailure("READER_SETTINGS_MISSING") from None
    except (OSError, json.JSONDecodeError):
        raise ReaderFailure("READER_SETTINGS_UNREADABLE") from None

    if not isinstance(parsed, dict) or parsed.get("schemaVersion") != 1:
        raise ReaderFailure("READER_SETTINGS_VERSION_UNSUPPORTED")
    if parsed.get("enabled") is not True:
        raise ReaderFailure("READER_DISABLED")
    account = parsed.get("accountExternalId")
    conversation = parsed.get("conversationExternalId")
    if not isinstance(account, str) or not account.strip() or len(account) > 512:
        raise ReaderFailure("READER_ACCOUNT_REQUIRED")
    transport = _validate_transport(parsed.get("transport"))
    configured_targets = parsed.get("conversationExternalIds")
    if configured_targets is not None:
        if (not isinstance(configured_targets, list)
                or len(configured_targets) > MAX_MONITORED_TARGETS
                or any(not isinstance(item, str) or not item.strip() or len(item) > 512 for item in configured_targets)):
            raise ReaderFailure("READER_TARGETS_INVALID")
        target_ids = [item.strip() for item in configured_targets]
    else:
        target_ids = []
    if isinstance(conversation, str) and conversation.strip():
        if len(conversation) > 512:
            raise ReaderFailure("READER_SCOPE_INVALID")
        legacy_id = conversation.strip()
        if legacy_id not in target_ids:
            target_ids.insert(0, legacy_id)
    elif conversation is not None and not isinstance(conversation, str):
        raise ReaderFailure("READER_SCOPE_INVALID")
    if len(target_ids) > MAX_MONITORED_TARGETS or len(set(target_ids)) != len(target_ids):
        raise ReaderFailure("READER_TARGETS_INVALID")
    if not target_ids and not transport["enabled"]:
        raise ReaderFailure("READER_CONVERSATION_REQUIRED")
    combined_scope = account + "".join(target_ids)
    if any(ord(character) < 32 for character in combined_scope):
        raise ReaderFailure("READER_SCOPE_INVALID")
    if any(value.endswith("@chatroom") or value.startswith("gh_") or value in EXCLUDED_DIRECT_IDS
           for value in target_ids):
        raise ReaderFailure("DIRECT_CONVERSATIONS_ONLY")
    return {
        "accountExternalId": account.strip(),
        "conversationExternalId": target_ids[0] if target_ids else "",
        "conversationExternalIds": target_ids,
        "transport": transport,
    }


def _import_readonly_module() -> Any:
    if os.name != "nt":
        raise ReaderFailure("WINDOWS_REQUIRED")
    package_root = RUNTIME_ROOT / "package"
    dependency_root = RUNTIME_ROOT / "deps"
    if not (package_root / "wechatauto" / "db.py").is_file():
        raise ReaderFailure("READER_RUNTIME_NOT_PREPARED")
    sys.path.insert(0, str(package_root))
    sys.path.insert(0, str(dependency_root))
    try:
        module = importlib.import_module("wechatauto.db")
    except Exception:
        raise ReaderFailure("READONLY_DATABASE_MODULE_UNAVAILABLE") from None
    if FORBIDDEN_MODULES.intersection(sys.modules):
        raise ReaderFailure("READONLY_MODULE_BOUNDARY_VIOLATION")
    return module


def _readonly_database_class(base_type: type) -> type:
    class ReceiveOnlyWeChatDB(base_type):
        def _stable_key_file(self, account: str | None = None) -> None:
            return None

        def _load_key_cache(self, path: str) -> dict:
            allowed = {
                os.path.normcase(os.path.abspath(self.keys_file)),
                os.path.normcase(os.path.abspath(self.keys_file + ".bak")),
            }
            if os.path.normcase(os.path.abspath(path)) not in allowed:
                return {}
            return super()._load_key_cache(path)

        def _save_keys(self) -> None:
            return None

        def _try_other_accounts(self) -> bool:
            return False

    return ReceiveOnlyWeChatDB


@contextlib.contextmanager
def _quiet_upstream_output() -> Iterator[None]:
    with open(os.devnull, "w", encoding="utf-8") as sink:
        with contextlib.redirect_stdout(sink), contextlib.redirect_stderr(sink):
            yield


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req: Any, fp: Any, code: int, msg: str, headers: Any, newurl: str) -> None:
        return None


def _remote_error_code(response_body: bytes) -> str | None:
    try:
        document = json.loads(response_body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if not isinstance(document, dict):
        return None
    error = document.get("error")
    candidate = error.get("code") if isinstance(error, dict) else document.get("code")
    if not isinstance(candidate, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", candidate):
        return None
    return candidate.upper().replace("-", "_")


def _success_receipt_summary(
    contract_name: str,
    payload: dict[str, Any],
    response_body: bytes,
    status: int,
) -> dict[str, Any]:
    try:
        receipt = json.loads(response_body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status}) from None
    if not isinstance(receipt, dict):
        raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})

    if contract_name == "RawMessageEventV1":
        match_status = receipt.get("matchStatus")
        created = receipt.get("created")
        if match_status not in {"Matched", "Created", "NeedsReview"} or not isinstance(created, bool):
            raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})
        summary: dict[str, Any] = {"matchStatus": match_status, "created": created}
        review_code = receipt.get("reviewCode")
        if isinstance(review_code, str) and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", review_code):
            summary["reviewCode"] = review_code.upper().replace("-", "_")
        return summary

    if contract_name == "HistoryBatchV1":
        expected_job_id = payload.get("jobId")
        history_status = receipt.get("status")
        is_last_batch = receipt.get("isLastBatch")
        numeric_fields = ("read", "matched", "created", "needsReview", "ignoredUnsupported", "failed")
        if (not isinstance(expected_job_id, str)
                or receipt.get("jobId") != expected_job_id
                or history_status not in {"Pending", "Running", "Completed", "Partial", "Failed", "Cancelled"}
                or not isinstance(is_last_batch, bool)
                or any(not isinstance(receipt.get(field), int) or isinstance(receipt.get(field), bool)
                       or receipt[field] < 0 for field in numeric_fields)):
            raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})
        return {
            "status": history_status,
            "read": receipt["read"],
            "matched": receipt["matched"],
            "created": receipt["created"],
            "needsReview": receipt["needsReview"],
            "ignoredUnsupported": receipt["ignoredUnsupported"],
            "failed": receipt["failed"],
            "isLastBatch": is_last_batch,
        }

    if contract_name == "ReaderStatusHeartbeatV1":
        if receipt.get("accepted") is not True:
            raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})
        return {"status": "accepted"}

    if contract_name == "ReaderDirectorySubmissionV1":
        discovered_count = receipt.get("discoveredConversationCount")
        if (receipt.get("accepted") is not True
                or not isinstance(discovered_count, int) or isinstance(discovered_count, bool)
                or discovered_count != len(payload.get("sessions", []))):
            raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})
        return {"status": "accepted", "discoveredConversationCount": discovered_count}

    if contract_name in {"ReaderDirectoryFailureV1", "ReaderHistoryFailureV1"}:
        if receipt.get("accepted") is not True:
            raise ReaderFailure("FORMAL_API_RECEIPT_INVALID", {"httpStatus": status})
        return {"status": "accepted"}

    raise ReaderFailure("READER_CONTRACT_UNSUPPORTED")


def _post_contract(settings: dict[str, Any], route: str, contract_name: str, payload: dict[str, Any]) -> int:
    transport = settings["transport"]
    if not transport["enabled"]:
        raise ReaderFailure("READER_TRANSPORT_DISABLED")
    url = transport["apiBaseUrl"] + route
    try:
        request_body = json.dumps(
            payload, ensure_ascii=False, separators=(",", ":"), allow_nan=False
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeEncodeError):
        raise ReaderFailure("READER_EVENT_SERIALIZATION_FAILED") from None

    request = urllib.request.Request(
        url,
        data=request_body,
        method="POST",
        headers={
            "Authorization": "Bearer " + transport["readerBearerToken"],
            "Accept": "application/json",
            "Content-Type": "application/json; charset=utf-8",
        },
    )
    opener = urllib.request.build_opener(_NoRedirectHandler())
    for attempt in range(3):
        try:
            with opener.open(request, timeout=5) as response:
                response_body = response.read(MAX_API_RESPONSE_BYTES + 1)
                status = int(response.status)
        except urllib.error.HTTPError as exc:
            response_body = exc.read(MAX_API_RESPONSE_BYTES + 1)
            status = int(exc.code)
            if (status in {408, 425, 429} or status >= 500) and attempt < 2:
                time.sleep(0.5 * (2 ** attempt))
                continue
            remote_code = _remote_error_code(response_body)
            details: dict[str, Any] = {"httpStatus": status}
            if remote_code is not None:
                details["remoteCode"] = remote_code
            raise ReaderFailure("FORMAL_API_REJECTED", details) from None
        except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException):
            if attempt < 2:
                time.sleep(0.5 * (2 ** attempt))
                continue
            raise ReaderFailure("FORMAL_API_UNAVAILABLE", {"attempts": 3}) from None

        if len(response_body) > MAX_API_RESPONSE_BYTES:
            raise ReaderFailure("FORMAL_API_RESPONSE_TOO_LARGE", {"httpStatus": status})
        if 200 <= status < 300:
            summary = _success_receipt_summary(contract_name, payload, response_body, status)
            _report_transport_success(contract_name, status, summary)
            return status
        remote_code = _remote_error_code(response_body)
        details = {"httpStatus": status}
        if remote_code is not None:
            details["remoteCode"] = remote_code
        raise ReaderFailure("FORMAL_API_REJECTED", details)

    raise ReaderFailure("FORMAL_API_UNAVAILABLE", {"attempts": 3})


def _get_server_targets(settings: dict[str, Any]) -> dict[str, Any]:
    transport = settings["transport"]
    if not transport["enabled"]:
        raise ReaderFailure("READER_TRANSPORT_DISABLED")
    request = urllib.request.Request(
        transport["apiBaseUrl"] + "/api/reader/targets",
        method="GET",
        headers={
            "Authorization": "Bearer " + transport["readerBearerToken"],
            "Accept": "application/json",
        },
    )
    opener = urllib.request.build_opener(_NoRedirectHandler())
    for attempt in range(3):
        try:
            with opener.open(request, timeout=5) as response:
                body = response.read(MAX_API_RESPONSE_BYTES + 1)
                status = int(response.status)
        except urllib.error.HTTPError as exc:
            body = exc.read(MAX_API_RESPONSE_BYTES + 1)
            status = int(exc.code)
            if (status in {408, 425, 429} or status >= 500) and attempt < 2:
                time.sleep(0.5 * (2 ** attempt))
                continue
            details: dict[str, Any] = {"httpStatus": status}
            remote_code = _remote_error_code(body)
            if remote_code is not None:
                details["remoteCode"] = remote_code
            raise ReaderFailure("READER_TARGET_SYNC_REJECTED", details) from None
        except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException):
            if attempt < 2:
                time.sleep(0.5 * (2 ** attempt))
                continue
            raise ReaderFailure("READER_TARGET_SYNC_UNAVAILABLE", {"attempts": 3}) from None

        if len(body) > MAX_API_RESPONSE_BYTES:
            raise ReaderFailure("FORMAL_API_RESPONSE_TOO_LARGE", {"httpStatus": status})
        if not 200 <= status < 300:
            raise ReaderFailure("READER_TARGET_SYNC_REJECTED", {"httpStatus": status})
        try:
            result = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ReaderFailure("READER_TARGET_RESPONSE_INVALID") from None
        if not isinstance(result, dict):
            raise ReaderFailure("READER_TARGET_RESPONSE_INVALID")
        account_external_id = result.get("accountExternalId")
        version = result.get("targetVersion")
        targets = result.get("conversationExternalIds")
        if (account_external_id != settings["accountExternalId"]
                or not isinstance(version, int) or isinstance(version, bool) or version < 0
                or not isinstance(targets, list) or len(targets) > MAX_MONITORED_TARGETS
                or any(not isinstance(value, str) or not value.strip() or len(value) > 512
                       or any(ord(character) < 32 for character in value)
                       or value.endswith("@chatroom") or value.startswith("gh_") or value in EXCLUDED_DIRECT_IDS
                       for value in targets)
                or len(set(targets)) != len(targets)):
            raise ReaderFailure("READER_TARGET_RESPONSE_INVALID")
        return {"targetVersion": version, "conversationExternalIds": list(targets)}

    raise ReaderFailure("READER_TARGET_SYNC_UNAVAILABLE", {"attempts": 3})


def _get_next_discovery_job(settings: dict[str, Any]) -> dict[str, str] | None:
    transport = settings["transport"]
    if not transport["enabled"]:
        return None
    request = urllib.request.Request(
        transport["apiBaseUrl"] + "/api/reader/discovery-jobs/next",
        method="GET",
        headers={
            "Authorization": "Bearer " + transport["readerBearerToken"],
            "Accept": "application/json",
        },
    )
    opener = urllib.request.build_opener(_NoRedirectHandler())
    try:
        with opener.open(request, timeout=5) as response:
            body = response.read(MAX_API_RESPONSE_BYTES + 1)
            status = int(response.status)
    except urllib.error.HTTPError as exc:
        details: dict[str, Any] = {"httpStatus": int(exc.code)}
        remote_code = _remote_error_code(exc.read(MAX_API_RESPONSE_BYTES + 1))
        if remote_code is not None:
            details["remoteCode"] = remote_code
        raise ReaderFailure("READER_DISCOVERY_POLL_REJECTED", details) from None
    except (urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException):
        raise ReaderFailure("READER_DISCOVERY_POLL_UNAVAILABLE") from None

    if len(body) > MAX_API_RESPONSE_BYTES or not 200 <= status < 300:
        raise ReaderFailure("READER_DISCOVERY_RESPONSE_INVALID")
    try:
        result = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ReaderFailure("READER_DISCOVERY_RESPONSE_INVALID") from None
    if not isinstance(result, dict):
        raise ReaderFailure("READER_DISCOVERY_RESPONSE_INVALID")
    job = result.get("job")
    if job is None:
        return None
    if (not isinstance(job, dict)
            or not isinstance(job.get("jobId"), str)
            or not re.fullmatch(r"[0-9a-fA-F-]{36}", job["jobId"])
            or job.get("accountExternalId") != settings["accountExternalId"]):
        raise ReaderFailure("READER_DISCOVERY_RESPONSE_INVALID")
    return {"jobId": job["jobId"], "accountExternalId": job["accountExternalId"]}


def _get_history_control(settings: dict[str, Any], path: str) -> dict[str, Any] | None:
    transport = settings["transport"]
    request = urllib.request.Request(transport["apiBaseUrl"] + path, method="GET", headers={
        "Authorization": "Bearer " + transport["readerBearerToken"], "Accept": "application/json"})
    try:
        with urllib.request.build_opener(_NoRedirectHandler()).open(request, timeout=5) as response:
            if int(response.status) == 204:
                return None
            body = response.read(MAX_API_RESPONSE_BYTES + 1)
            if len(body) > MAX_API_RESPONSE_BYTES:
                raise ReaderFailure("READER_HISTORY_RESPONSE_INVALID")
            result = json.loads(body.decode("utf-8"))
            if not isinstance(result, dict):
                raise ReaderFailure("READER_HISTORY_RESPONSE_INVALID")
            return result
    except ReaderFailure:
        raise
    except (ValueError, UnicodeDecodeError, urllib.error.URLError, TimeoutError, OSError, http.client.HTTPException):
        raise ReaderFailure("READER_HISTORY_POLL_UNAVAILABLE") from None


def _run_queued_history(settings: dict[str, Any], db_module: Any, stop: threading.Event) -> None:
    """Run a claimed job on a separate database instance; realtime stays responsive."""
    job = _get_history_control(settings, "/api/reader/history-jobs/next")
    if job is None:
        return
    if (not isinstance(job.get("jobId"), str) or not re.fullmatch(r"[0-9a-fA-F-]{36}", job["jobId"])
            or job.get("accountExternalId") != settings["accountExternalId"]
            or not isinstance(job.get("conversationExternalId"), str)
            or not isinstance(job.get("maximumRows"), int) or isinstance(job["maximumRows"], bool)
            or not 1 <= job["maximumRows"] <= MAX_HISTORY_ROWS):
        raise ReaderFailure("READER_HISTORY_RESPONSE_INVALID")
    route = "/api/reader/history-jobs/" + job["jobId"]
    target = job["conversationExternalId"]
    database = None
    workdir = None
    try:
        if target not in _get_server_targets(settings)["conversationExternalIds"]:
            raise ReaderFailure("READER_SCOPE_NOT_ALLOWED")
        from_utc = _parse_utc(job.get("fromUtc"), "from-utc")
        to_utc = _parse_utc(job.get("toUtc"), "to-utc")
        if from_utc is None or to_utc is None or from_utc > to_utc or to_utc - from_utc > dt.timedelta(days=90):
            raise ReaderFailure("INVALID_HISTORY_RANGE")
        scoped = {**settings, "conversationExternalId": target}
        database, workdir = _open_database(scoped, db_module)

        def send(items: list[dict[str, Any]], is_last: bool) -> bool:
            if stop.is_set():
                raise ReaderFailure("READER_HISTORY_INTERRUPTED")
            control = _get_history_control(settings, route)
            if control is None or control.get("status") != "Running":
                return False
            if target not in _get_server_targets(settings)["conversationExternalIds"]:
                raise ReaderFailure("READER_SCOPE_NOT_ALLOWED")
            _post_contract(settings, route + "/batches", "HistoryBatchV1", {
                "schemaVersion": 1, "jobId": job["jobId"], "accountExternalId": settings["accountExternalId"],
                "conversationExternalId": target,
                "requestedRange": {"fromUtc": _utc_text(from_utc), "toUtc": _utc_text(to_utc)},
                "items": items, "isLastBatch": is_last})
            return True

        # Stream the scan into 100-row uploads: peak memory follows the batch size, and a window
        # larger than one page finishes instead of being rejected. The final batch is always sent,
        # even when empty, because that is the server's proof the range was fully scanned.
        pending: list[dict[str, Any]] = []
        for event in _read_history(database, scoped, from_utc, to_utc, job["maximumRows"]):
            pending.append(event)
            if len(pending) >= 100:
                if not send(pending, False):
                    return
                pending = []
        send(pending, True)
    except ReaderFailure as exc:
        try:
            _post_contract(settings, route + "/fail", "ReaderHistoryFailureV1", {"errorCode": exc.code})
        except ReaderFailure:
            pass
    except Exception:
        try:
            _post_contract(settings, route + "/fail", "ReaderHistoryFailureV1", {"errorCode": "READER_HISTORY_FAILED"})
        except ReaderFailure:
            pass
    finally:
        if workdir is not None:
            shutil.rmtree(workdir, ignore_errors=True)


def _discover_session_directory(database: Any) -> list[dict[str, str | None]]:
    try:
        with _quiet_upstream_output():
            raw_sessions = database.get_sessions(limit=MAX_DISCOVERY_SESSIONS)
    except Exception:
        raise ReaderFailure("READER_DIRECTORY_READ_FAILED") from None
    if not isinstance(raw_sessions, list) or len(raw_sessions) > MAX_DISCOVERY_SESSIONS:
        raise ReaderFailure("READER_DIRECTORY_INVALID")

    result: list[dict[str, str | None]] = []
    seen: set[str] = set()
    for row in raw_sessions:
        if not isinstance(row, dict):
            continue
        conversation_id = str(row.get("username") or "").strip()
        if (not conversation_id or len(conversation_id) > 512
                or any(ord(character) < 32 for character in conversation_id)
                or conversation_id.endswith("@chatroom") or conversation_id.startswith("gh_")
                or conversation_id in EXCLUDED_DIRECT_IDS or conversation_id in seen):
            continue
        seen.add(conversation_id)
        try:
            with _quiet_upstream_output():
                display_name = database.get_nickname(conversation_id)
        except Exception:
            display_name = None
        if not isinstance(display_name, str) or display_name == conversation_id:
            display_name = None
        elif len(display_name) > 200 or any(ord(character) < 32 for character in display_name):
            display_name = None
        result.append({"conversationExternalId": conversation_id, "displayName": display_name})
    return result


def _report_transport_success(contract_name: str, status: int, receipt_summary: dict[str, Any]) -> None:
    destination = sys.__stderr__ or sys.stderr
    summary = {
        "transport": {
            "status": "accepted",
            "contract": contract_name,
            "httpStatus": status,
            **receipt_summary,
        }
    }
    print(json.dumps(summary, ensure_ascii=False, separators=(",", ":")), file=destination, flush=True)


def _open_database(settings: dict[str, Any], db_module: Any) -> tuple[Any, Path]:
    with _quiet_upstream_output():
        data_dir = db_module.auto_detect_db_dir()
    if not data_dir:
        raise ReaderFailure("WECHAT_DATABASE_NOT_FOUND")
    workdir = Path(tempfile.mkdtemp(prefix="weca-reader-"))
    key_path = workdir / "ephemeral-keys.json"
    try:
        with _quiet_upstream_output():
            database = _readonly_database_class(db_module.WeChatDB)(
                db_dir=data_dir,
                account=settings["accountExternalId"],
                workdir=str(workdir),
                keys_file=str(key_path),
            )
            if str(database.account) != settings["accountExternalId"]:
                raise ReaderFailure("ACTIVE_ACCOUNT_MISMATCH")
            conversation_ids = {
                str(row.get("username") or "").strip()
                for row in database.get_sessions(limit=2000)
                if isinstance(row, dict)
            }
            missing_conversations = set(settings.get("conversationExternalIds", [])) - conversation_ids
            if missing_conversations:
                raise ReaderFailure("DIRECT_CONVERSATION_NOT_FOUND")
        return database, workdir
    except ReaderFailure:
        shutil.rmtree(workdir, ignore_errors=True)
        raise
    except Exception:
        shutil.rmtree(workdir, ignore_errors=True)
        raise ReaderFailure("WECHAT_DATABASE_UNAVAILABLE") from None


def _resolve_scope_for_contact(
    database: Any,
    display_name: str,
    wechat_executable_path: str | None,
) -> dict[str, str]:
    name = display_name.strip() if isinstance(display_name, str) else ""
    if not name or len(name) > 200 or any(ord(character) < 32 for character in name):
        raise ReaderFailure("SCOPE_DISPLAY_NAME_INVALID")

    account_id = str(getattr(database, "account", "") or "").strip()
    if not account_id or len(account_id) > 512 or any(ord(character) < 32 for character in account_id):
        raise ReaderFailure("SCOPE_ACTIVE_ACCOUNT_UNAVAILABLE")

    self_wxid = str(getattr(database, "wxid", "") or "").strip()
    try:
        with _quiet_upstream_output():
            self_info = database.get_self_info()
    except Exception:
        raise ReaderFailure("SCOPE_ACTIVE_ACCOUNT_UNVERIFIED") from None
    if not isinstance(self_info, dict) or not self_wxid or str(self_info.get("username") or "") != self_wxid:
        raise ReaderFailure("SCOPE_ACTIVE_ACCOUNT_UNVERIFIED")

    try:
        with _quiet_upstream_output():
            connection = database._contact_conn()
        if connection is None:
            raise ReaderFailure("SCOPE_CONTACT_DATABASE_UNAVAILABLE")
        try:
            rows = connection.execute(
                "SELECT username, nick_name, remark FROM contact WHERE remark=? OR nick_name=?",
                (name, name),
            ).fetchall()
        finally:
            connection.close()
    except ReaderFailure:
        raise
    except Exception:
        raise ReaderFailure("SCOPE_CONTACT_LOOKUP_FAILED") from None

    matches: set[str] = set()
    for row in rows:
        username = str(row["username"] or "").strip()
        primary_name = str(row["remark"] or row["nick_name"] or username).strip()
        if (primary_name != name or not username or len(username) > 512
                or any(ord(character) < 32 for character in username)
                or username.endswith("@chatroom") or username.startswith("gh_")
                or username in EXCLUDED_DIRECT_IDS):
            continue
        matches.add(username)

    if not matches:
        raise ReaderFailure("SCOPE_CONTACT_NOT_FOUND")
    if len(matches) != 1:
        raise ReaderFailure("SCOPE_CONTACT_AMBIGUOUS")

    executable = (wechat_executable_path or "").strip()
    if len(executable) > 1024 or any(ord(character) < 32 for character in executable):
        raise ReaderFailure("SCOPE_WECHAT_PATH_INVALID")
    return {
        "provider": PROVIDER,
        "accountExternalId": account_id,
        "conversationExternalId": next(iter(matches)),
        "displayName": name,
        "wechatExecutablePath": executable,
    }


def _run_scope_resolution(args: argparse.Namespace, db_module: Any) -> None:
    request_path = Path(os.path.abspath(args.request_file))
    output_path = Path(os.path.abspath(args.output))
    request_parent = os.path.normcase(os.path.abspath(os.path.dirname(args.request_file)))
    output_parent = os.path.normcase(os.path.abspath(os.path.dirname(args.output)))
    state_root = Path(request_parent)
    is_private_formal_root = (
        state_root.name.casefold() == "formal"
        and state_root.parent.name.casefold() == "wechataicopilot"
    )
    if (not is_private_formal_root or request_parent != output_parent
            or request_path.name != "scope-request.tmp.json"
            or output_path.name != "scope.tmp.json"):
        raise ReaderFailure("SCOPE_BOOTSTRAP_PATH_INVALID")

    try:
        request = json.loads(request_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        raise ReaderFailure("SCOPE_BOOTSTRAP_REQUEST_INVALID") from None
    if not isinstance(request, dict):
        raise ReaderFailure("SCOPE_BOOTSTRAP_REQUEST_INVALID")

    with _quiet_upstream_output():
        data_dir = db_module.auto_detect_db_dir()
    if not data_dir:
        raise ReaderFailure("WECHAT_DATABASE_NOT_FOUND")

    workdir = Path(tempfile.mkdtemp(prefix="weca-scope-"))
    try:
        with _quiet_upstream_output():
            database = _readonly_database_class(db_module.WeChatDB)(
                db_dir=data_dir,
                workdir=str(workdir),
                keys_file=str(workdir / "ephemeral-keys.json"),
            )
        scope = _resolve_scope_for_contact(
            database,
            request.get("displayName"),
            request.get("wechatExecutablePath"),
        )
        output_path.write_text(
            json.dumps(scope, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
    except ReaderFailure:
        raise
    except Exception:
        raise ReaderFailure("SCOPE_RESOLUTION_FAILED") from None
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


def _run_list_contacts(args: argparse.Namespace, db_module: Any) -> None:
    """List the direct conversations available on this machine, for an interactive picker.

    Chatrooms and official accounts are already excluded by the discovery helper. The result is
    written to a file inside the private state directory instead of stdout so contact names never
    end up in a shell log.
    """
    output_path = Path(os.path.abspath(args.output))
    output_parent = os.path.normcase(os.path.abspath(os.path.dirname(args.output)))
    state_root = Path(output_parent)
    is_private_formal_root = (
        state_root.name.casefold() == "formal"
        and state_root.parent.name.casefold() == "wechataicopilot"
    )
    if not is_private_formal_root or output_path.name != "contacts.tmp.json":
        raise ReaderFailure("CONTACTS_LIST_PATH_INVALID")

    with _quiet_upstream_output():
        data_dir = db_module.auto_detect_db_dir()
    if not data_dir:
        raise ReaderFailure("WECHAT_DATABASE_NOT_FOUND")

    workdir = Path(tempfile.mkdtemp(prefix="weca-contacts-"))
    try:
        with _quiet_upstream_output():
            database = _readonly_database_class(db_module.WeChatDB)(
                db_dir=data_dir,
                workdir=str(workdir),
                keys_file=str(workdir / "ephemeral-keys.json"),
            )
        contacts = _discover_session_directory(database)
        output_path.write_text(
            json.dumps({"items": contacts}, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
    except ReaderFailure:
        raise
    except Exception:
        raise ReaderFailure("CONTACTS_LIST_FAILED") from None
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


def _as_string(value: Any) -> str | None:
    if value is None:
        return None
    return str(value)


def _timestamp(value: Any) -> dt.datetime | None:
    try:
        epoch = int(value)
    except (TypeError, ValueError, OverflowError):
        return None
    if epoch > 10_000_000_000:
        epoch //= 1000
    if epoch < MIN_SOURCE_EPOCH or epoch > MAX_SOURCE_EPOCH:
        return None
    return dt.datetime.fromtimestamp(epoch, tz=dt.timezone.utc)


def _utc_text(value: dt.datetime | None) -> str | None:
    if value is None:
        return None
    return value.isoformat(timespec="auto").replace("+00:00", "Z")


def _parse_utc(value: str | None, option: str) -> dt.datetime | None:
    if value is None:
        return None
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise ReaderFailure(f"INVALID_{option.upper().replace('-', '_')}") from None
    if parsed.tzinfo is None or parsed.utcoffset() != dt.timedelta(0):
        raise ReaderFailure("HISTORY_RANGE_MUST_BE_UTC")
    parsed = parsed.astimezone(dt.timezone.utc)
    if parsed.timestamp() < MIN_SOURCE_EPOCH or parsed.timestamp() > MAX_SOURCE_EPOCH:
        raise ReaderFailure("HISTORY_RANGE_OUTSIDE_SUPPORTED_SOURCE_TIME")
    return parsed


def _time_predicate(from_utc: dt.datetime | None, to_utc: dt.datetime | None) -> tuple[str, list[int]] | None:
    if from_utc is None and to_utc is None:
        return None

    clauses: list[str] = []
    params: list[int] = []
    for scale, minimum, maximum in ((1, MIN_SOURCE_EPOCH, MAX_SOURCE_EPOCH), (1000, MIN_SOURCE_EPOCH * 1000, MAX_SOURCE_EPOCH * 1000)):
        bounds = [f"CAST(create_time AS INTEGER) >= {minimum}", f"CAST(create_time AS INTEGER) <= {maximum}"]
        if from_utc is not None:
            bounds.append("CAST(create_time AS INTEGER) >= ?")
            params.append(int(from_utc.timestamp()) * scale)
        if to_utc is not None:
            bounds.append("CAST(create_time AS INTEGER) <= ?")
            params.append(int(to_utc.timestamp()) * scale)
        clauses.append("(" + " AND ".join(bounds) + ")")
    return "(" + " OR ".join(clauses) + ")", params


def _quoted_table(value: Any) -> str:
    table = str(value or "")
    if not TABLE_NAME_RE.fullmatch(table):
        raise ReaderFailure("MESSAGE_TABLE_UNSUPPORTED")
    return '"' + table.replace('"', '""') + '"'


def _shard_relative_name(connection: sqlite3.Connection) -> str | None:
    try:
        row = connection.execute("PRAGMA database_list").fetchone()
    except sqlite3.DatabaseError:
        return None
    if row is None or len(row) < 3 or not row[2]:
        return None
    filename = Path(str(row[2])).name
    if filename.startswith("message__"):
        relative = "message/" + filename[len("message__"):]
    elif re.fullmatch(r"message_\d+\.db", filename, re.IGNORECASE):
        relative = "message/" + filename
    else:
        return None
    if not re.fullmatch(r"message/message_\d+\.db", relative, re.IGNORECASE):
        return None
    return relative.casefold()


def _shard_fingerprint(account: str, connection: sqlite3.Connection) -> str | None:
    relative = _shard_relative_name(connection)
    if relative is None:
        return None
    return hashlib.sha256(f"wechat-local\0{account}\0{relative}".encode("utf-8")).hexdigest()


def _identity_namespace(account: str, conversation: str, shard: str | None, table: str | None) -> str:
    parts = [account, conversation, shard or "unknown-shard", table or "unknown-table"]
    stable_scope = json.dumps(parts, ensure_ascii=False, separators=(",", ":"))
    return "wechat-local-sender.v1:" + hashlib.sha256(stable_scope.encode("utf-8")).hexdigest()


def _external_message_id(account: str, conversation: str, local_id: Any, sort_seq: Any) -> str | None:
    if local_id is None or sort_seq is None:
        return None
    stable_key = "|".join((account, conversation, str(local_id or ""), str(sort_seq or "")))
    return "wx4:" + hashlib.sha256(stable_key.encode("utf-8")).hexdigest()


def _message_type(source_type: Any) -> str:
    if isinstance(source_type, int):
        return "Text" if source_type == TEXT_LOCAL_TYPE else "Other"
    return MESSAGE_TYPES.get(str(source_type), "Other")


def _raw_event(
    settings: dict[str, Any],
    source_kind: str,
    row: dict[str, Any],
    source_identity: dict[str, str | None],
) -> dict[str, Any]:
    message_type = _message_type(row.get("type"))
    source_time = _timestamp(row.get("create_time"))
    content = row.get("content")
    text = content if message_type == "Text" and isinstance(content, str) else None
    table = source_identity.get("messageTable")
    shard = source_identity.get("shardFingerprint")
    local_id = source_identity.get("localId")
    sort_seq = source_identity.get("sortSeq")
    return {
        "schemaVersion": 1,
        "provider": PROVIDER,
        "sourceKind": source_kind,
        "accountExternalId": settings["accountExternalId"],
        "conversationExternalId": settings["conversationExternalId"],
        "conversationType": "Direct",
        "messageType": message_type,
        "text": text,
        "sourceOccurredAt": _utc_text(source_time),
        "rawSenderId": _as_string(row.get("sender_id")),
        "identityNamespace": _identity_namespace(
            settings["accountExternalId"], settings["conversationExternalId"], shard, table
        ),
        "sourceIdentity": {
            "localId": local_id,
            "sortSeq": sort_seq,
            "serverId": source_identity.get("serverId"),
            "shardFingerprint": shard,
            "messageTable": table,
        },
        "externalMessageId": _external_message_id(
            settings["accountExternalId"], settings["conversationExternalId"], local_id, sort_seq
        ),
        "direction": "Unknown",
        "directionEvidence": "Unresolved",
    }


def _source_identity_from_raw(
    account: str,
    connection: sqlite3.Connection,
    table: str,
    raw: Any,
) -> dict[str, str | None]:
    return {
        "localId": _as_string(raw["local_id"]),
        "sortSeq": _as_string(raw["sort_seq"]),
        "serverId": _as_string(raw["server_id"]),
        "shardFingerprint": _shard_fingerprint(account, connection),
        "messageTable": table,
    }


def _realtime_source_identity(database: Any, settings: dict[str, Any], row: dict[str, Any]) -> dict[str, str | None]:
    identity: dict[str, str | None] = {
        "localId": _as_string(row.get("local_id")),
        "sortSeq": _as_string(row.get("sort_seq")),
        "serverId": None,
        "shardFingerprint": None,
        "messageTable": None,
    }
    if row.get("local_id") is None or row.get("sort_seq") is None:
        return identity

    matches: list[dict[str, str | None]] = []
    connections: list[tuple[sqlite3.Connection, str]] = []
    try:
        with _quiet_upstream_output():
            connections = database._msg_conns(settings["conversationExternalId"])
            for connection, table_value in connections:
                table = str(table_value)
                quoted = _quoted_table(table)
                found = connection.execute(
                    f"SELECT local_id, sort_seq, server_id FROM {quoted} WHERE local_id=? AND sort_seq=? LIMIT 2",
                    (row.get("local_id"), row.get("sort_seq")),
                ).fetchall()
                for raw in found:
                    matches.append(_source_identity_from_raw(settings["accountExternalId"], connection, table, raw))
    except Exception:
        return identity
    finally:
        for connection, _ in connections:
            try:
                connection.close()
            except Exception:
                pass

    if len(matches) == 1:
        identity.update(matches[0])
        return identity
    if matches:
        table_values = {match["messageTable"] for match in matches}
        server_values = {match["serverId"] for match in matches}
        identity["messageTable"] = next(iter(table_values)) if len(table_values) == 1 else None
        identity["serverId"] = next(iter(server_values)) if len(server_values) == 1 else None
    return identity


def _emit_json_line(value: dict[str, Any], lock: threading.Lock | None = None) -> None:
    output = sys.__stdout__ or sys.stdout
    line = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
    if lock is None:
        print(line, file=output, flush=True)
    else:
        with lock:
            print(line, file=output, flush=True)


def _run_realtime(settings: dict[str, Any], db_module: Any) -> None:
    database, workdir = _open_database(settings, db_module)
    listener = None
    output_lock = threading.Lock()
    target_lock = threading.RLock()
    callback_failed = threading.Event()
    callback_failure: list[ReaderFailure] = []
    callbacks: dict[str, Any] = {}
    poll_status_lock = threading.Lock()
    poll_error_code: list[str | None] = [None]
    started_at = dt.datetime.now(tz=dt.timezone.utc)
    reader_instance_id = str(uuid.uuid4())
    current_version = 0
    last_error_code: str | None = None
    final_state = "Stopped"
    final_error_code: str | None = None
    history_stop = threading.Event()
    history_thread: threading.Thread | None = None
    try:
        with _quiet_upstream_output():
            local_app_data = Path(os.environ["LOCALAPPDATA"])
            watermark_directory = local_app_data / "WeChatAiCopilot" / "Reader"
            watermark_directory.mkdir(parents=True, exist_ok=True)
            listener = db_module.Listener(
                database,
                interval=0.75,
                watermark_file=str(watermark_directory / "listener-watermarks.json"),
                max_retries=0,
            )
            original_poll_once = listener._poll_once

            def synchronized_poll_once() -> None:
                with target_lock:
                    original_poll_once()

            # Serialize callback-map changes with Listener's poll snapshot.
            listener._poll_once = synchronized_poll_once

            def monitored_poll_loop() -> None:
                while not listener._stop.is_set():
                    try:
                        listener._poll_once()
                        with poll_status_lock:
                            poll_error_code[0] = None
                    except Exception:
                        with poll_status_lock:
                            poll_error_code[0] = "READER_POLL_FAILED"
                    listener._stop.wait(listener.interval)

            # The pinned Listener logs and suppresses poll exceptions internally.
            # Keep its per-session dispatch behavior but report only a sanitized
            # poll failure code through the Reader heartbeat.
            listener._run = monitored_poll_loop
            def get_available_conversations() -> set[str]:
                with _quiet_upstream_output():
                    return {
                        str(row.get("username") or "").strip()
                        for row in database.get_sessions(limit=2000)
                        if isinstance(row, dict)
                    }

            def make_callback(conversation_external_id: str) -> Any:
                scoped_settings = {**settings, "conversationExternalId": conversation_external_id}

                def on_message(row: dict[str, Any], _listener: Any) -> None:
                    try:
                        identity = _realtime_source_identity(database, scoped_settings, row)
                        event = _raw_event(scoped_settings, "Realtime", row, identity)
                        if settings["transport"]["enabled"]:
                            _post_contract(settings, "/api/reader/messages", "RawMessageEventV1", event)
                        else:
                            _emit_json_line(event, output_lock)
                    except ReaderFailure as exc:
                        if (exc.code == "FORMAL_API_REJECTED"
                                and exc.details.get("remoteCode") == "READER_SCOPE_NOT_ALLOWED"):
                            # A target may be removed while an already-dispatched
                            # callback is finishing. Treat that revocation as an
                            # intentional drop for this target only.
                            return
                        callback_failure.append(exc)
                        callback_failed.set()
                        raise
                    except Exception:
                        callback_failure.append(ReaderFailure("REALTIME_EVENT_READ_FAILED"))
                        callback_failed.set()
                        raise

                return on_message

            def apply_targets(target_ids: list[str]) -> None:
                if len(target_ids) > MAX_MONITORED_TARGETS or len(set(target_ids)) != len(target_ids):
                    raise ReaderFailure("READER_TARGETS_INVALID")
                if set(target_ids) - get_available_conversations():
                    raise ReaderFailure("DIRECT_CONVERSATION_NOT_FOUND")
                desired = set(target_ids)
                with target_lock:
                    for conversation_external_id in list(callbacks):
                        if conversation_external_id not in desired:
                            listener.remove_listener(conversation_external_id, callbacks.pop(conversation_external_id))
                    for conversation_external_id in target_ids:
                        if conversation_external_id not in callbacks:
                            callback = make_callback(conversation_external_id)
                            listener.add_listener(conversation_external_id, callback)
                            callbacks[conversation_external_id] = callback

            if settings["transport"]["enabled"]:
                try:
                    initial_targets = _get_server_targets(settings)
                    current_version = initial_targets["targetVersion"]
                    apply_targets(initial_targets["conversationExternalIds"])
                except ReaderFailure as exc:
                    last_error_code = exc.code
            else:
                apply_targets(list(settings["conversationExternalIds"]))
            listener.start()
            next_sync_at = 0.0
            while not callback_failed.is_set():
                managed_stop = Path(os.environ["LOCALAPPDATA"]) / "WeChatAiCopilot" / "Formal" / ("reader-stop-" + str(os.getpid()) + ".request")
                if managed_stop.is_file() and managed_stop.stat().st_mtime >= started_at.timestamp():
                    managed_stop.unlink(missing_ok=True)
                    break
                now = time.monotonic()
                if settings["transport"]["enabled"] and now >= next_sync_at:
                    next_sync_at = now + 5
                    if history_thread is None or not history_thread.is_alive():
                        def history_worker() -> None:
                            try:
                                _run_queued_history(settings, db_module, history_stop)
                            except ReaderFailure:
                                pass  # Only sanitized heartbeat/receipt metadata is logged.
                        history_thread = threading.Thread(target=history_worker, daemon=True, name="history-worker")
                        history_thread.start()
                    try:
                        target_snapshot = _get_server_targets(settings)
                        target_version = target_snapshot["targetVersion"]
                        target_ids = target_snapshot["conversationExternalIds"]
                        if target_version < current_version:
                            raise ReaderFailure("READER_TARGET_VERSION_REVERSED")
                        if target_version == current_version and set(target_ids) != set(callbacks):
                            raise ReaderFailure("READER_TARGET_VERSION_REUSED")
                        if target_version != current_version:
                            apply_targets(target_ids)
                            current_version = target_version
                        last_error_code = None
                    except ReaderFailure as exc:
                        last_error_code = exc.code

                    with poll_status_lock:
                        current_poll_error = poll_error_code[0]
                    if current_poll_error is not None:
                        last_error_code = current_poll_error

                    try:
                        discovery_job = _get_next_discovery_job(settings)
                        if discovery_job is not None:
                            try:
                                sessions = _discover_session_directory(database)
                                _post_contract(settings,
                                    "/api/reader/discovery-jobs/" + urllib.parse.quote(discovery_job["jobId"], safe="") + "/sessions",
                                    "ReaderDirectorySubmissionV1",
                                    {"accountExternalId": settings["accountExternalId"], "sessions": sessions})
                            except ReaderFailure as exc:
                                try:
                                    _post_contract(settings,
                                        "/api/reader/discovery-jobs/" + urllib.parse.quote(discovery_job["jobId"], safe="") + "/fail",
                                        "ReaderDirectoryFailureV1", {"errorCode": exc.code})
                                except ReaderFailure:
                                    pass
                    except ReaderFailure as exc:
                        last_error_code = exc.code

                    state = "Error" if last_error_code else ("Listening" if callbacks else "Idle")
                    heartbeat = {
                        "readerInstanceId": reader_instance_id,
                        "accountExternalId": settings["accountExternalId"],
                        "state": state,
                        "targetVersion": current_version,
                        "listeningTargetCount": len(callbacks),
                        "lastErrorCode": last_error_code,
                        "startedAtUtc": _utc_text(started_at),
                    }
                    try:
                        _post_contract(settings, "/api/reader/status", "ReaderStatusHeartbeatV1", heartbeat)
                    except ReaderFailure as exc:
                        last_error_code = exc.code
                if callback_failed.wait(1):
                    break

            if callback_failure:
                final_state = "Error"
                final_error_code = callback_failure[0].code
                raise callback_failure[0]
    except KeyboardInterrupt:
        final_state = "Stopped"
    except ReaderFailure as exc:
        final_state = "Error"
        final_error_code = exc.code
        raise
    except Exception:
        final_state = "Error"
        final_error_code = "REALTIME_CAPTURE_FAILED"
        raise ReaderFailure("REALTIME_CAPTURE_FAILED") from None
    finally:
        history_stop.set()
        if history_thread is not None:
            history_thread.join(timeout=6)
        if settings["transport"]["enabled"]:
            try:
                _post_contract(settings, "/api/reader/status", "ReaderStatusHeartbeatV1", {
                    "readerInstanceId": reader_instance_id,
                    "accountExternalId": settings["accountExternalId"],
                    "state": final_state,
                    "targetVersion": current_version,
                    "listeningTargetCount": 0 if final_state == "Stopped" else len(callbacks),
                    "lastErrorCode": final_error_code,
                    "startedAtUtc": _utc_text(started_at),
                })
            except ReaderFailure:
                pass
        if listener is not None:
            try:
                with _quiet_upstream_output():
                    listener.stop()
            except Exception:
                pass
        shutil.rmtree(workdir, ignore_errors=True)


def _query_clause(from_utc: dt.datetime | None, to_utc: dt.datetime | None) -> tuple[str, list[int]] | None:
    predicate = _time_predicate(from_utc, to_utc)
    return predicate


def _read_history(
    database: Any,
    settings: dict[str, Any],
    from_utc: dt.datetime | None,
    to_utc: dt.datetime | None,
    max_rows: int,
) -> Iterator[dict[str, Any]]:
    """Yield history events for the requested window using keyset paging.

    ``max_rows`` bounds one page of work per shard, not the size of the window. Paging on
    ``local_id`` (the message table's integer primary key) keeps each read cheap and indexable,
    avoids large offsets, and lets a window larger than one page complete instead of aborting.
    """
    page_size = max(1, min(max_rows, HISTORY_SCAN_PAGE_ROWS))
    events_streamed = 0
    connections: list[tuple[sqlite3.Connection, str]] = []
    try:
        with _quiet_upstream_output():
            connections = database._msg_conns(settings["conversationExternalId"])
            time_clause = _query_clause(from_utc, to_utc)
            for connection, table_value in connections:
                table = str(table_value)
                quoted = _quoted_table(table)
                cursor = -1
                while True:
                    where = "local_type=? AND local_id>?"
                    params: list[Any] = [TEXT_LOCAL_TYPE, cursor]
                    if time_clause is not None:
                        where += " AND " + time_clause[0]
                        params.extend(time_clause[1])
                    params.append(page_size)
                    raw_rows = connection.execute(
                        "SELECT local_id, local_type, real_sender_id, create_time, message_content, "
                        "compress_content, server_id, sort_seq "
                        f"FROM {quoted} WHERE {where} ORDER BY local_id ASC LIMIT ?",
                        params,
                    ).fetchall()
                    if not raw_rows:
                        break
                    for raw in raw_rows:
                        source_time = _timestamp(raw["create_time"])
                        if from_utc is not None and (source_time is None or source_time < from_utc):
                            continue
                        if to_utc is not None and (source_time is None or source_time > to_utc):
                            continue
                        normalized = database._msg_row_to_dict(raw)
                        identity = _source_identity_from_raw(
                            settings["accountExternalId"], connection, table, raw
                        )
                        events_streamed += 1
                        if events_streamed > HISTORY_JOB_ROW_GUARD:
                            raise ReaderFailure("HISTORY_JOB_ROW_GUARD_EXCEEDED")
                        yield _raw_event(settings, "HistoryBackfill", normalized, identity)
                    last_id = raw_rows[-1]["local_id"]
                    try:
                        cursor = int(last_id)
                    except (TypeError, ValueError):
                        raise ReaderFailure("HISTORY_SCAN_CURSOR_INVALID") from None
                    if len(raw_rows) < page_size:
                        break
    except ReaderFailure:
        raise
    except Exception:
        raise ReaderFailure("HISTORY_READ_FAILED") from None
    finally:
        for connection, _ in connections:
            try:
                connection.close()
            except Exception:
                pass


def _post_history_batch(
    settings: dict[str, Any],
    job_id: str,
    from_utc: dt.datetime | None,
    to_utc: dt.datetime | None,
    items: list[dict[str, Any]],
    is_last_batch: bool,
) -> None:
    batch = {
        "schemaVersion": 1,
        "jobId": job_id,
        "accountExternalId": settings["accountExternalId"],
        "conversationExternalId": settings["conversationExternalId"],
        "requestedRange": {
            "fromUtc": _utc_text(from_utc),
            "toUtc": _utc_text(to_utc),
        },
        "items": items,
        "isLastBatch": is_last_batch,
    }
    if settings["transport"]["enabled"]:
        _post_contract(settings, "/api/reader/history-batches", "HistoryBatchV1", batch)
    else:
        _emit_json_line(batch)


def _run_history(args: argparse.Namespace, settings: dict[str, Any], db_module: Any) -> None:
    if not args.job_id.strip() or len(args.job_id) > 128:
        raise ReaderFailure("HISTORY_JOB_ID_REQUIRED")
    if not settings.get("conversationExternalId"):
        raise ReaderFailure("HISTORY_CONVERSATION_REQUIRED")
    if not 1 <= args.max_rows <= MAX_HISTORY_ROWS:
        raise ReaderFailure("HISTORY_MAX_ROWS_OUT_OF_RANGE")
    if not 1 <= args.batch_size <= MAX_BATCH_SIZE:
        raise ReaderFailure("HISTORY_BATCH_SIZE_OUT_OF_RANGE")
    from_utc = _parse_utc(args.from_utc, "from-utc")
    to_utc = _parse_utc(args.to_utc, "to-utc")
    if from_utc is not None and to_utc is not None and from_utc > to_utc:
        raise ReaderFailure("INVALID_HISTORY_RANGE")

    database, workdir = _open_database(settings, db_module)
    try:
        # Upload as the scan progresses so peak memory follows the batch size rather than the size
        # of the requested window. The final batch is always sent, even when it is empty, because
        # that is what tells the server this range has been fully scanned.
        pending: list[dict[str, Any]] = []
        for event in _read_history(database, settings, from_utc, to_utc, args.max_rows):
            pending.append(event)
            if len(pending) >= args.batch_size:
                _post_history_batch(settings, args.job_id, from_utc, to_utc, pending, False)
                pending = []
        _post_history_batch(settings, args.job_id, from_utc, to_utc, pending, True)
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Read-only local WeChat source adapter for M1.")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("realtime", help="emit RawMessageEventV1 JSON lines for the configured direct conversation")
    resolve_scope = subparsers.add_parser("resolve-scope", help="resolve one exact approved direct contact name without reading message content")
    resolve_scope.add_argument("--request-file", required=True)
    resolve_scope.add_argument("--output", required=True)
    contacts = subparsers.add_parser("list-contacts", help="list readable direct conversations into a private state file")
    contacts.add_argument("--output", required=True)
    history = subparsers.add_parser("history", help="emit bounded HistoryBatchV1 JSON lines")
    history.add_argument("--job-id", required=True)
    history.add_argument("--from-utc")
    history.add_argument("--to-utc")
    history.add_argument("--max-rows", type=int, default=MAX_HISTORY_ROWS)
    history.add_argument("--batch-size", type=int, default=100)
    return parser


def main() -> int:
    args = _build_parser().parse_args()
    try:
        if os.name != "nt":
            raise ReaderFailure("WINDOWS_REQUIRED")
        if args.command == "resolve-scope":
            _run_scope_resolution(args, _import_readonly_module())
            return 0
        if args.command == "list-contacts":
            _run_list_contacts(args, _import_readonly_module())
            return 0
        settings = _load_settings()
        db_module = _import_readonly_module()
        if args.command == "realtime":
            _run_realtime(settings, db_module)
        elif args.command == "history":
            _run_history(args, settings, db_module)
        return 0
    except ReaderFailure as exc:
        _error(exc.code)
        return 2
    except Exception:
        _error("READER_FAILED")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
