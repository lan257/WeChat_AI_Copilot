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
from pathlib import Path
from typing import Any, Iterator


READER_ROOT = Path(__file__).resolve().parent
RUNTIME_ROOT = READER_ROOT / ".runtime"
PROVIDER = "local-wechat"
MIN_SOURCE_EPOCH = 946684800
MAX_SOURCE_EPOCH = 4102444800
MAX_HISTORY_ROWS = 5000
MAX_BATCH_SIZE = 500
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
    if not isinstance(conversation, str) or not conversation.strip() or len(conversation) > 512:
        raise ReaderFailure("READER_CONVERSATION_REQUIRED")
    if any(ord(character) < 32 for character in account + conversation):
        raise ReaderFailure("READER_SCOPE_INVALID")
    if conversation.endswith("@chatroom") or conversation.startswith("gh_") or conversation in EXCLUDED_DIRECT_IDS:
        raise ReaderFailure("DIRECT_CONVERSATIONS_ONLY")
    return {
        "accountExternalId": account.strip(),
        "conversationExternalId": conversation.strip(),
        "transport": _validate_transport(parsed.get("transport")),
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
            if settings["conversationExternalId"] not in conversation_ids:
                raise ReaderFailure("DIRECT_CONVERSATION_NOT_FOUND")
        return database, workdir
    except ReaderFailure:
        shutil.rmtree(workdir, ignore_errors=True)
        raise
    except Exception:
        shutil.rmtree(workdir, ignore_errors=True)
        raise ReaderFailure("WECHAT_DATABASE_UNAVAILABLE") from None


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
    callback_failed = threading.Event()
    callback_failure: list[ReaderFailure] = []
    try:
        with _quiet_upstream_output():
            listener = db_module.Listener(database, interval=0.75, watermark_file="", max_retries=0)

            def on_message(row: dict[str, Any], _listener: Any) -> None:
                try:
                    identity = _realtime_source_identity(database, settings, row)
                    event = _raw_event(settings, "Realtime", row, identity)
                    if settings["transport"]["enabled"]:
                        _post_contract(settings, "/api/reader/messages", "RawMessageEventV1", event)
                    else:
                        _emit_json_line(event, output_lock)
                except ReaderFailure as exc:
                    callback_failure.append(exc)
                    callback_failed.set()
                    raise
                except Exception:
                    callback_failure.append(ReaderFailure("REALTIME_EVENT_READ_FAILED"))
                    callback_failed.set()
                    raise

            listener.add_listener(settings["conversationExternalId"], on_message)
            listener.start()
            while not callback_failed.wait(1):
                pass
            raise callback_failure[0] if callback_failure else ReaderFailure("REALTIME_EVENT_READ_FAILED")
    except KeyboardInterrupt:
        return
    except ReaderFailure:
        raise
    except Exception:
        raise ReaderFailure("REALTIME_CAPTURE_FAILED") from None
    finally:
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


def _sort_seq_key(value: str | None) -> tuple[int, Any]:
    if value is None:
        return (2, "")
    try:
        return (0, int(value))
    except (TypeError, ValueError):
        return (1, value)


def _read_history(
    database: Any,
    settings: dict[str, Any],
    from_utc: dt.datetime | None,
    to_utc: dt.datetime | None,
    max_rows: int,
) -> list[dict[str, Any]]:
    events: list[tuple[tuple[Any, ...], dict[str, Any]]] = []
    connections: list[tuple[sqlite3.Connection, str]] = []
    try:
        with _quiet_upstream_output():
            connections = database._msg_conns(settings["conversationExternalId"])
            time_clause = _query_clause(from_utc, to_utc)
            for connection, table_value in connections:
                table = str(table_value)
                quoted = _quoted_table(table)
                where = "local_type=?"
                params: list[Any] = [TEXT_LOCAL_TYPE]
                if time_clause is not None:
                    where += " AND " + time_clause[0]
                    params.extend(time_clause[1])
                params.append(max_rows + 1)
                raw_rows = connection.execute(
                    "SELECT local_id, local_type, real_sender_id, create_time, message_content, "
                    "compress_content, server_id, sort_seq "
                    f"FROM {quoted} WHERE {where} ORDER BY sort_seq ASC, local_id ASC LIMIT ?",
                    params,
                ).fetchall()
                if len(raw_rows) > max_rows:
                    raise ReaderFailure("HISTORY_ROW_LIMIT_EXCEEDED")
                for raw in raw_rows:
                    normalized = database._msg_row_to_dict(raw)
                    source_time = _timestamp(raw["create_time"])
                    if from_utc is not None and (source_time is None or source_time < from_utc):
                        continue
                    if to_utc is not None and (source_time is None or source_time > to_utc):
                        continue
                    identity = _source_identity_from_raw(
                        settings["accountExternalId"], connection, table, raw
                    )
                    event = _raw_event(settings, "HistoryBackfill", normalized, identity)
                    sort_key = (
                        source_time or dt.datetime.min.replace(tzinfo=dt.timezone.utc),
                        _sort_seq_key(identity["sortSeq"]),
                        identity["shardFingerprint"] or "",
                        identity["localId"] or "",
                    )
                    events.append((sort_key, event))
                    if len(events) > max_rows:
                        raise ReaderFailure("HISTORY_ROW_LIMIT_EXCEEDED")
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

    events.sort(key=lambda item: item[0])
    return [event for _, event in events]


def _run_history(args: argparse.Namespace, settings: dict[str, Any], db_module: Any) -> None:
    if not args.job_id.strip() or len(args.job_id) > 128:
        raise ReaderFailure("HISTORY_JOB_ID_REQUIRED")
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
        rows = _read_history(database, settings, from_utc, to_utc, args.max_rows)
        batches = [rows[index:index + args.batch_size] for index in range(0, len(rows), args.batch_size)]
        if not batches:
            batches = [[]]
        for index, items in enumerate(batches):
            batch = {
                "schemaVersion": 1,
                "jobId": args.job_id,
                "accountExternalId": settings["accountExternalId"],
                "conversationExternalId": settings["conversationExternalId"],
                "requestedRange": {
                    "fromUtc": _utc_text(from_utc),
                    "toUtc": _utc_text(to_utc),
                },
                "items": items,
                "isLastBatch": index == len(batches) - 1,
            }
            if settings["transport"]["enabled"]:
                _post_contract(
                    settings,
                    "/api/reader/history-batches",
                    "HistoryBatchV1",
                    batch,
                )
            else:
                _emit_json_line(batch)
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Read-only local WeChat source adapter for M1.")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("realtime", help="emit RawMessageEventV1 JSON lines for the configured direct conversation")
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
