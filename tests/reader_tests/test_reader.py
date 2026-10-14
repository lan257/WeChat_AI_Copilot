"""Offline tests for the formal M1 Reader adapter."""

from __future__ import annotations

import datetime as dt
import email.message
import importlib.util
import io
import json
import os
import sqlite3
import sys
import tempfile
import threading
import unittest
import urllib.error
from argparse import Namespace
from pathlib import Path
from unittest import mock


PRODUCT_ROOT = Path(__file__).resolve().parents[2]
READER_PATH = PRODUCT_ROOT / "src" / "WeChatAiCopilot.Reader" / "reader.py"
SPEC = importlib.util.spec_from_file_location("wechat_ai_copilot_reader", READER_PATH)
assert SPEC is not None and SPEC.loader is not None
reader = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = reader
SPEC.loader.exec_module(reader)


class FakeResponse:
    def __init__(self, status: int = 202, body: bytes = b"{}") -> None:
        self.status = status
        self._body = body

    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def read(self, _size: int = -1) -> bytes:
        return self._body


class RecordingOpener:
    def __init__(self, outcomes: list[object] | None = None) -> None:
        self.requests = []
        self.outcomes = list(outcomes or [])

    def open(self, request: object, timeout: int) -> FakeResponse:
        self.requests.append((request, timeout))
        if self.outcomes:
            outcome = self.outcomes.pop(0)
        elif getattr(request, "full_url", "").endswith("/api/reader/status"):
            outcome = FakeResponse(200, b'{"accepted":true}')
        elif getattr(request, "full_url", "").endswith("/sessions"):
            payload = json.loads(request.data.decode("utf-8"))  # type: ignore[union-attr]
            outcome = FakeResponse(200, json.dumps({
                "accepted": True,
                "discoveredConversationCount": len(payload["sessions"]),
            }).encode("utf-8"))
        elif getattr(request, "full_url", "").endswith("/api/reader/messages"):
            outcome = FakeResponse(202, b'{"matchStatus":"Created","created":true}')
        else:
            payload = json.loads(request.data.decode("utf-8"))  # type: ignore[union-attr]
            item_count = len(payload["items"])
            status = "Completed" if payload["isLastBatch"] else "Running"
            receipt = {
                "jobId": payload["jobId"],
                "read": item_count,
                "matched": 0,
                "created": item_count,
                "needsReview": 0,
                "ignoredUnsupported": 0,
                "failed": 0,
                "status": status,
                "isLastBatch": payload["isLastBatch"],
            }
            outcome = FakeResponse(200, json.dumps(receipt).encode("utf-8"))
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome  # type: ignore[return-value]


class FakeScopeDatabase:
    account = "wxid_owner_abcd"
    wxid = "wxid_owner"

    def __init__(self, contacts: list[tuple[str, str, str]]) -> None:
        self.connection = sqlite3.connect(":memory:")
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("CREATE TABLE contact (username TEXT, nick_name TEXT, remark TEXT)")
        self.connection.executemany(
            "INSERT INTO contact (username, nick_name, remark) VALUES (?, ?, ?)", contacts
        )

    def get_self_info(self) -> dict[str, str]:
        return {"username": self.wxid}

    def _contact_conn(self) -> sqlite3.Connection:
        return self.connection


class FakeScopeBootstrapDatabase(FakeScopeDatabase):
    def __init__(self, **_kwargs: object) -> None:
        super().__init__([("wxid_target", "合成联系人甲", "")])


class ReaderConfigTests(unittest.TestCase):
    def test_scope_bootstrap_uses_only_private_fixed_request_and_output_files(self) -> None:
        class FakeDatabaseModule:
            WeChatDB = FakeScopeBootstrapDatabase

            @staticmethod
            def auto_detect_db_dir() -> str:
                return "fake-wechat-data"

        with tempfile.TemporaryDirectory() as temp_dir:
            state_root = Path(temp_dir) / "WeChatAiCopilot" / "Formal"
            state_root.mkdir(parents=True)
            request_path = state_root / "scope-request.tmp.json"
            output_path = state_root / "scope.tmp.json"
            request_path.write_text(json.dumps({
                "displayName": "合成联系人甲",
                "wechatExecutablePath": "",
            }), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                reader._run_scope_resolution(
                    Namespace(request_file=str(request_path), output=str(output_path)),
                    FakeDatabaseModule,
                )

            scope = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(scope["provider"], "local-wechat")
            self.assertEqual(scope["conversationExternalId"], "wxid_target")
            self.assertEqual(scope["displayName"], "合成联系人甲")
            self.assertNotIn("messages", scope)

    def test_scope_resolution_returns_only_exact_direct_contact(self) -> None:
        database = FakeScopeDatabase([
            ("wxid_target", "合成联系人甲", ""),
            ("wxid_other", "另一个联系人", ""),
            ("team@chatroom", "合成联系人甲", ""),
        ])

        scope = reader._resolve_scope_for_contact(
            database, "合成联系人甲", r"C:\Program Files\Tencent\WeChat\Weixin.exe"
        )

        self.assertEqual(scope["provider"], "local-wechat")
        self.assertEqual(scope["accountExternalId"], "wxid_owner_abcd")
        self.assertEqual(scope["conversationExternalId"], "wxid_target")
        self.assertEqual(scope["displayName"], "合成联系人甲")

    def test_scope_resolution_rejects_ambiguous_exact_contact_names(self) -> None:
        database = FakeScopeDatabase([
            ("wxid_first", "同名联系人", ""),
            ("wxid_second", "同名联系人", ""),
        ])

        with self.assertRaisesRegex(reader.ReaderFailure, "SCOPE_CONTACT_AMBIGUOUS"):
            reader._resolve_scope_for_contact(database, "同名联系人", None)

    def test_scope_resolution_rejects_contacts_not_found_or_wrong_account_identity(self) -> None:
        database = FakeScopeDatabase([("wxid_target", "其他联系人", "")])
        with self.assertRaisesRegex(reader.ReaderFailure, "SCOPE_CONTACT_NOT_FOUND"):
            reader._resolve_scope_for_contact(database, "指定联系人", None)

        database = FakeScopeDatabase([("wxid_target", "指定联系人", "")])
        database.get_self_info = lambda: {"username": "wxid_different"}  # type: ignore[method-assign]
        with self.assertRaisesRegex(reader.ReaderFailure, "SCOPE_ACTIVE_ACCOUNT_UNVERIFIED"):
            reader._resolve_scope_for_contact(database, "指定联系人", None)


    def test_api_base_url_requires_https_for_remote_hosts(self) -> None:
        self.assertEqual(
            reader._validate_api_base_url(" http://localhost:5080/ "),
            "http://localhost:5080",
        )
        self.assertEqual(
            reader._validate_api_base_url("https://api.example.test/v1/"),
            "https://api.example.test/v1",
        )
        with self.assertRaisesRegex(reader.ReaderFailure, "HTTPS_REQUIRED_FOR_REMOTE_READER_API"):
            reader._validate_api_base_url("http://api.example.test")

    def test_enabled_transport_rejects_short_token_and_url_credentials(self) -> None:
        with self.assertRaisesRegex(reader.ReaderFailure, "READER_BEARER_TOKEN_INVALID"):
            reader._validate_transport({
                "enabled": True,
                "apiBaseUrl": "https://api.example.test",
                "readerBearerToken": "too-short",
            })
        with self.assertRaisesRegex(reader.ReaderFailure, "READER_API_BASE_URL_INVALID"):
            reader._validate_api_base_url("https://user:password@api.example.test")

    def test_load_settings_trims_direct_scope_and_rejects_group_chat(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            config_path = Path(temp_dir) / "WeChatAiCopilot" / "Reader" / "settings.json"
            config_path.parent.mkdir(parents=True)
            document = {
                "schemaVersion": 1,
                "enabled": True,
                "accountExternalId": "  account-1  ",
                "conversationExternalId": "  contact-1  ",
                "transport": {"enabled": False},
            }
            config_path.write_text(json.dumps(document), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                settings = reader._load_settings()
            self.assertEqual(settings["accountExternalId"], "account-1")
            self.assertEqual(settings["conversationExternalId"], "contact-1")
            self.assertEqual(settings["transport"], {
                "enabled": False,
                "apiBaseUrl": "",
                "readerBearerToken": "",
            })

            document["conversationExternalId"] = "room@chatroom"
            config_path.write_text(json.dumps(document), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                with self.assertRaisesRegex(reader.ReaderFailure, "DIRECT_CONVERSATIONS_ONLY"):
                    reader._load_settings()

    def test_server_managed_multi_target_settings_can_start_without_a_static_session(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            config_path = Path(temp_dir) / "WeChatAiCopilot" / "Reader" / "settings.json"
            config_path.parent.mkdir(parents=True)
            config_path.write_text(json.dumps({
                "schemaVersion": 1,
                "enabled": True,
                "accountExternalId": "account-1",
                "conversationExternalId": "",
                "conversationExternalIds": [],
                "transport": {
                    "enabled": True,
                    "apiBaseUrl": "http://127.0.0.1:5080",
                    "readerBearerToken": "t" * 40,
                },
            }), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                settings = reader._load_settings()
            self.assertEqual(settings["accountExternalId"], "account-1")
            self.assertEqual(settings["conversationExternalIds"], [])
            self.assertEqual(settings["conversationExternalId"], "")

    def test_server_managed_targets_are_bounded_and_direct_only(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            config_path = Path(temp_dir) / "WeChatAiCopilot" / "Reader" / "settings.json"
            config_path.parent.mkdir(parents=True)
            document = {
                "schemaVersion": 1,
                "enabled": True,
                "accountExternalId": "account-1",
                "conversationExternalId": "",
                "conversationExternalIds": ["first", "first"],
                "transport": {
                    "enabled": True,
                    "apiBaseUrl": "http://127.0.0.1:5080",
                    "readerBearerToken": "t" * 40,
                },
            }
            config_path.write_text(json.dumps(document), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                with self.assertRaisesRegex(reader.ReaderFailure, "READER_TARGETS_INVALID"):
                    reader._load_settings()

            document["conversationExternalIds"] = ["room@chatroom"]
            config_path.write_text(json.dumps(document), encoding="utf-8")
            with mock.patch.dict(os.environ, {"LOCALAPPDATA": temp_dir}):
                with self.assertRaisesRegex(reader.ReaderFailure, "DIRECT_CONVERSATIONS_ONLY"):
                    reader._load_settings()


class ReaderContractTests(unittest.TestCase):
    def history_job(self) -> dict[str, object]:
        return {"jobId": "a21b2460-9228-4e03-b714-fd54098575dc", "accountExternalId": self.settings["accountExternalId"],
                "conversationExternalId": "test-authorized-contact", "fromUtc": "2026-09-01T00:00:00Z",
                "toUtc": "2026-10-01T00:00:00Z", "maximumRows": 5000}

    def test_history_queue_rejects_wrong_account_before_local_read(self) -> None:
        job = self.history_job()
        job["accountExternalId"] = "another-account"
        with mock.patch.object(reader, "_get_history_control", return_value=job), mock.patch.object(reader, "_open_database") as opened:
            with self.assertRaises(reader.ReaderFailure):
                reader._run_queued_history(self.settings, object(), threading.Event())
        opened.assert_not_called()

    def test_history_queue_cancellation_keeps_batches_unsubmitted(self) -> None:
        job = self.history_job()
        with tempfile.TemporaryDirectory() as workdir, \
                mock.patch.object(reader, "_get_history_control", side_effect=[job, {"status": "Cancelled"}]), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": [job["conversationExternalId"]]}), \
                mock.patch.object(reader, "_open_database", return_value=(object(), Path(workdir))), \
                mock.patch.object(reader, "_read_history", return_value=[{"synthetic": True}]), \
                mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
            post.assert_not_called()

    def test_history_queue_empty_range_submits_real_final_zero_batch(self) -> None:
        job = self.history_job()
        with tempfile.TemporaryDirectory() as workdir, \
                mock.patch.object(reader, "_get_history_control", side_effect=[job, {"status": "Running"}]), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": [job["conversationExternalId"]]}), \
                mock.patch.object(reader, "_open_database", return_value=(object(), Path(workdir))), \
                mock.patch.object(reader, "_read_history", return_value=[]), \
                mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
            payload = post.call_args.args[3]
            self.assertEqual([], payload["items"])
            self.assertTrue(payload["isLastBatch"])
            self.assertEqual(job["jobId"], payload["jobId"])

    def test_history_queue_reads_every_local_message_and_resumes_from_server_cursors(self) -> None:
        job = {"jobId": "a21b2460-9228-4e03-b714-fd54098575dc", "accountExternalId": self.settings["accountExternalId"],
               "conversationExternalId": "test-authorized-contact", "fromUtc": None, "toUtc": None,
               "maximumRows": reader.MAX_FULL_HISTORY_ROWS, "fullLocalHistory": True,
               "resumeCursors": [{"messageTable": "Msg_0000000000000000001", "lastLocalId": 42},
                                 {"messageTable": "bad", "lastLocalId": -1}]}
        seen: dict[str, object] = {}

        def scan(database, settings, from_utc, to_utc, max_rows, resume):
            seen.update({"from_utc": from_utc, "to_utc": to_utc, "max_rows": max_rows, "resume": resume})
            return []

        with tempfile.TemporaryDirectory() as workdir, \
                mock.patch.object(reader, "_get_history_control", side_effect=[job, {"status": "Running"}]), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": [job["conversationExternalId"]]}), \
                mock.patch.object(reader, "_open_database", return_value=(object(), Path(workdir))), \
                mock.patch.object(reader, "_read_history", side_effect=scan), \
                mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
        self.assertIsNone(seen["from_utc"])
        self.assertIsNone(seen["to_utc"])
        self.assertEqual(reader.MAX_FULL_HISTORY_ROWS, seen["max_rows"])
        # Only well formed cursors carry over; a negative offset must not skip unread rows.
        self.assertEqual({"Msg_0000000000000000001": 42}, seen["resume"])
        self.assertEqual({"fromUtc": None, "toUtc": None}, post.call_args.args[3]["requestedRange"])

    def test_history_queue_rejects_half_open_window(self) -> None:
        job = self.history_job()
        job["toUtc"] = None
        with mock.patch.object(reader, "_get_history_control", return_value=job), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": [job["conversationExternalId"]]}), \
                mock.patch.object(reader, "_open_database") as opened, \
                mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
        opened.assert_not_called()
        self.assertEqual("INVALID_HISTORY_RANGE", post.call_args.args[3]["errorCode"])

    def test_history_queue_keeps_the_ninety_day_limit_for_bounded_windows(self) -> None:
        job = self.history_job()
        job["fromUtc"] = "2024-01-01T00:00:00Z"
        with mock.patch.object(reader, "_get_history_control", return_value=job), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": [job["conversationExternalId"]]}), \
                mock.patch.object(reader, "_open_database") as opened, \
                mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
        opened.assert_not_called()
        self.assertEqual("INVALID_HISTORY_RANGE", post.call_args.args[3]["errorCode"])

    def test_resume_cursors_ignore_malformed_entries(self) -> None:
        self.assertEqual({}, reader._resume_cursors(None))
        self.assertEqual({}, reader._resume_cursors("nope"))
        self.assertEqual({"Msg_2": 7}, reader._resume_cursors(
            [{"messageTable": "Msg_2", "lastLocalId": 7}, {"messageTable": "", "lastLocalId": 3},
             {"messageTable": "Msg_3", "lastLocalId": "x"}, {"messageTable": "Msg_4", "lastLocalId": True},
             {"messageTable": "Msg_5"}]))

    def test_history_queue_revoked_target_prevents_local_read(self) -> None:
        with mock.patch.object(reader, "_get_history_control", return_value=self.history_job()), \
                mock.patch.object(reader, "_get_server_targets", return_value={"conversationExternalIds": []}), \
                mock.patch.object(reader, "_open_database") as opened, mock.patch.object(reader, "_post_contract") as post:
            reader._run_queued_history(self.settings, object(), threading.Event())
            opened.assert_not_called()
            self.assertEqual("READER_SCOPE_NOT_ALLOWED", post.call_args.args[3]["errorCode"])

    def setUp(self) -> None:
        self.settings = {
            "accountExternalId": "sample-account",
            "conversationExternalId": "sample-contact",
            "transport": {
                "enabled": True,
                "apiBaseUrl": "http://127.0.0.1:5080",
                "readerBearerToken": "t" * 40,
            },
        }

    def make_event(self, local_id: int) -> dict[str, object]:
        row = {
            "type": 1,
            "create_time": int(dt.datetime(2026, 10, 9, 2, tzinfo=dt.timezone.utc).timestamp()),
            "content": f"消息 {local_id}，你好",
            "sender_id": "73",
        }
        identity = {
            "localId": str(local_id),
            "sortSeq": str(90000 + local_id),
            "serverId": "server-1",
            "shardFingerprint": "a" * 64,
            "messageTable": "Msg_0123456789abcdef0123456789abcdef",
        }
        return reader._raw_event(self.settings, "Realtime", row, identity)

    def make_history_event(self, local_id: int) -> dict[str, object]:
        row = {
            "type": 1,
            "create_time": int(dt.datetime(2026, 10, 9, 2, tzinfo=dt.timezone.utc).timestamp()),
            "content": f"历史样例 {local_id}，你好",
            "sender_id": "73",
        }
        identity = {
            "localId": str(local_id),
            "sortSeq": str(90000 + local_id),
            "serverId": "server-1",
            "shardFingerprint": "a" * 64,
            "messageTable": "Msg_0123456789abcdef0123456789abcdef",
        }
        return reader._raw_event(self.settings, "HistoryBackfill", row, identity)

    def test_d01_event_serializes_as_utf8_and_posts_with_reader_auth(self) -> None:
        event = self.make_event(58)
        opener = RecordingOpener()
        stderr = io.StringIO()
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", stderr):
            status = reader._post_contract(
                self.settings, "/api/reader/messages", "RawMessageEventV1", event
            )

        self.assertEqual(status, 202)
        request, timeout = opener.requests[0]
        self.assertEqual(timeout, 5)
        self.assertEqual(request.full_url, "http://127.0.0.1:5080/api/reader/messages")
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.get_header("Authorization"), "Bearer " + "t" * 40)
        self.assertEqual(request.get_header("Content-type"), "application/json; charset=utf-8")
        self.assertIn("你好".encode("utf-8"), request.data)
        self.assertEqual(json.loads(request.data.decode("utf-8")), event)
        self.assertEqual(event["provider"], "local-wechat")
        self.assertEqual(event["direction"], "Unknown")
        self.assertTrue(str(event["externalMessageId"]).startswith("wx4:"))
        self.assertIn('"contract":"RawMessageEventV1"', stderr.getvalue())
        self.assertNotIn("t" * 40, stderr.getvalue())

    def test_reader_status_heartbeat_uses_authenticated_json_without_chat_data(self) -> None:
        opener = RecordingOpener()
        stderr = io.StringIO()
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", stderr):
            status = reader._post_contract(self.settings, "/api/reader/status", "ReaderStatusHeartbeatV1", {
                "readerInstanceId": "00000000-0000-0000-0000-000000000001",
                "accountExternalId": self.settings["accountExternalId"],
                "state": "Listening",
                "targetVersion": 3,
                "listeningTargetCount": 2,
                "lastErrorCode": None,
                "startedAtUtc": "2026-10-10T00:00:00Z",
            })
        self.assertEqual(status, 200)
        request, _ = opener.requests[0]
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.get_header("Authorization"), "Bearer " + "t" * 40)
        self.assertEqual(json.loads(request.data.decode("utf-8"))["listeningTargetCount"], 2)
        self.assertNotIn("text", json.loads(request.data.decode("utf-8")))
        self.assertNotIn("t" * 40, stderr.getvalue())

    def test_target_sync_checks_account_scope_and_reports_multiple_direct_targets(self) -> None:
        body = json.dumps({
            "accountId": "00000000-0000-0000-0000-000000000001",
            "accountExternalId": "sample-account",
            "targetVersion": 7,
            "conversationExternalIds": ["sample-contact", "second-contact"],
        }).encode("utf-8")
        opener = RecordingOpener([FakeResponse(200, body)])
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener):
            targets = reader._get_server_targets(self.settings)
        self.assertEqual(targets["targetVersion"], 7)
        self.assertEqual(targets["conversationExternalIds"], ["sample-contact", "second-contact"])
        request, timeout = opener.requests[0]
        self.assertEqual(request.get_method(), "GET")
        self.assertEqual(timeout, 5)
        self.assertEqual(request.get_header("Authorization"), "Bearer " + "t" * 40)

    def test_reader_claims_only_account_bound_directory_job(self) -> None:
        job_id = "12345678-1234-4234-8234-123456789012"
        payload = json.dumps({"job": {
            "jobId": job_id,
            "accountExternalId": self.settings["accountExternalId"],
        }}).encode("utf-8")
        opener = RecordingOpener([FakeResponse(200, payload)])
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener):
            job = reader._get_next_discovery_job(self.settings)
        self.assertEqual(job, {
            "jobId": job_id,
            "accountExternalId": self.settings["accountExternalId"],
        })
        request, _ = opener.requests[0]
        self.assertEqual(request.get_method(), "GET")
        self.assertEqual(request.get_header("Authorization"), "Bearer " + "t" * 40)

    def test_directory_discovery_sends_only_direct_ids_and_display_names(self) -> None:
        class LocalDirectory:
            def get_sessions(self, limit: int) -> list[dict[str, object]]:
                self.asserted_limit = limit
                return [
                    {"username": "direct-1", "summary": "private message summary"},
                    {"username": "room@chatroom", "summary": "group content"},
                    {"username": "filehelper", "summary": "system content"},
                ]

            def get_nickname(self, username: str) -> str:
                return "Contact One" if username == "direct-1" else username

        result = reader._discover_session_directory(LocalDirectory())
        self.assertEqual(result, [{"conversationExternalId": "direct-1", "displayName": "Contact One"}])
        self.assertNotIn("summary", json.dumps(result))

    def test_wx4_legacy_external_id_matches_golden_fixture_exactly(self) -> None:
        row = {
            "type": 1,
            "create_time": int(dt.datetime(2026, 10, 9, 2, tzinfo=dt.timezone.utc).timestamp()),
            "content": "fixture message",
            "sender_id": "73",
        }
        identity = {
            "localId": "58",
            "sortSeq": "90001",
            "serverId": None,
            "shardFingerprint": "a" * 64,
            "messageTable": "Msg_fixture",
        }

        event = reader._raw_event(self.settings, "Realtime", row, identity)

        self.assertEqual(
            event["externalMessageId"],
            "wx4:cde6f87071b0390f847eedecfb2c5a4825ef2a3b49c2da85c9b413b0444a5925",
        )

    def test_needs_review_receipt_is_reported_without_message_identifiers(self) -> None:
        receipt = {
            "matchStatus": "NeedsReview",
            "created": False,
            "reviewCode": "SOURCE_SCOPE_INCOMPLETE",
            "messageId": "private-message-id",
            "messageCursor": "private-cursor",
        }
        opener = RecordingOpener([FakeResponse(202, json.dumps(receipt).encode("utf-8"))])
        stderr = io.StringIO()
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", stderr):
            reader._post_contract(
                self.settings, "/api/reader/messages", "RawMessageEventV1", self.make_event(58)
            )

        self.assertIn('"matchStatus":"NeedsReview"', stderr.getvalue())
        self.assertIn('"reviewCode":"SOURCE_SCOPE_INCOMPLETE"', stderr.getvalue())
        self.assertNotIn("private-message-id", stderr.getvalue())
        self.assertNotIn("private-cursor", stderr.getvalue())
        self.assertNotIn("t" * 40, stderr.getvalue())

    def test_temporary_http_failure_retries_then_succeeds(self) -> None:
        headers = email.message.Message()
        retryable = urllib.error.HTTPError(
            "http://127.0.0.1:5080/api/reader/messages",
            503,
            "service unavailable",
            headers,
            io.BytesIO(b'{"error":{"code":"busy"}}'),
        )
        opener = RecordingOpener([
            retryable,
            FakeResponse(200, b'{"matchStatus":"Created","created":true}'),
        ])
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.time, "sleep") as sleep, \
                mock.patch.object(reader.sys, "__stderr__", io.StringIO()):
            status = reader._post_contract(
                self.settings, "/api/reader/messages", "RawMessageEventV1", self.make_event(1)
            )
        self.assertEqual(status, 200)
        self.assertEqual(len(opener.requests), 2)
        sleep.assert_called_once_with(0.5)

    def test_d02_history_batch_transport_splits_rows_and_marks_last_batch(self) -> None:
        events = [self.make_history_event(index) for index in range(5)]
        opener = RecordingOpener()
        database = object()
        workdir = Path("unused-test-workdir")
        args = Namespace(
            job_id="job-offline-test",
            max_rows=10,
            batch_size=2,
            from_utc=None,
            to_utc=None,
        )
        with mock.patch.object(reader, "_open_database", return_value=(database, workdir)), \
                mock.patch.object(reader, "_read_history", return_value=events), \
                mock.patch.object(reader.shutil, "rmtree") as remove_workdir, \
                mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", io.StringIO()):
            reader._run_history(args, self.settings, object())

        self.assertEqual(len(opener.requests), 3)
        remove_workdir.assert_called_once_with(workdir, ignore_errors=True)
        batches = []
        for request, timeout in opener.requests:
            self.assertEqual(timeout, 5)
            self.assertEqual(request.full_url, "http://127.0.0.1:5080/api/reader/history-batches")
            batch = json.loads(request.data.decode("utf-8"))
            self.assertEqual(batch["jobId"], "job-offline-test")
            self.assertEqual(batch["accountExternalId"], "sample-account")
            self.assertEqual(batch["conversationExternalId"], "sample-contact")
            self.assertEqual(batch["schemaVersion"], 1)
            batches.append(batch)
        self.assertEqual([len(batch["items"]) for batch in batches], [2, 2, 1])
        self.assertEqual([batch["isLastBatch"] for batch in batches], [False, False, True])
        self.assertEqual([item["sourceIdentity"]["localId"] for batch in batches for item in batch["items"]],
                         ["0", "1", "2", "3", "4"])
        self.assertTrue(all(item["sourceKind"] == "HistoryBackfill"
                            for batch in batches for item in batch["items"]))

    def test_history_scan_pages_beyond_one_page_instead_of_failing(self) -> None:
        connection = sqlite3.connect(":memory:")
        connection.row_factory = sqlite3.Row
        connection.execute(
            "CREATE TABLE Msg_0123456789abcdef0123456789abcdef (local_id INTEGER PRIMARY KEY, local_type INTEGER, "
            "real_sender_id INTEGER, create_time INTEGER, message_content TEXT, "
            "compress_content TEXT, server_id TEXT, sort_seq INTEGER)"
        )
        stamp = int(dt.datetime(2026, 10, 9, 2, tzinfo=dt.timezone.utc).timestamp())
        connection.executemany(
            "INSERT INTO Msg_0123456789abcdef0123456789abcdef VALUES (?, 1, 73, ?, '合成正文', '', ?, ?)",
            [(index, stamp, f"server-{index}", 90000 + index) for index in range(1, 1201)],
        )

        class PagingDatabase:
            def _msg_conns(self, _conversation: str) -> list[tuple[sqlite3.Connection, str]]:
                return [(connection, "Msg_0123456789abcdef0123456789abcdef")]

            def _msg_row_to_dict(self, raw: sqlite3.Row) -> dict[str, object]:
                return dict(raw)

        events = list(reader._read_history(PagingDatabase(), self.settings, None, None, 5000))
        self.assertEqual(len(events), 1200)
        self.assertEqual([event["sourceIdentity"]["localId"] for event in events][:3], ["1", "2", "3"])
        self.assertEqual(events[-1]["sourceIdentity"]["localId"], "1200")
        connection.close()

    def test_history_scan_time_window_still_bounds_results(self) -> None:
        connection = sqlite3.connect(":memory:")
        connection.row_factory = sqlite3.Row
        connection.execute(
            "CREATE TABLE Msg_0123456789abcdef0123456789abcdef (local_id INTEGER PRIMARY KEY, local_type INTEGER, "
            "real_sender_id INTEGER, create_time INTEGER, message_content TEXT, "
            "compress_content TEXT, server_id TEXT, sort_seq INTEGER)"
        )
        october = int(dt.datetime(2026, 10, 9, 2, tzinfo=dt.timezone.utc).timestamp())
        january = int(dt.datetime(2026, 1, 4, 2, tzinfo=dt.timezone.utc).timestamp())
        connection.executemany(
            "INSERT INTO Msg_0123456789abcdef0123456789abcdef VALUES (?, 1, 73, ?, '合成正文', '', ?, ?)",
            [(1, january, "server-1", 90001), (2, october, "server-2", 90002), (3, october, "server-3", 90003)],
        )

        class PagingDatabase:
            def _msg_conns(self, _conversation: str) -> list[tuple[sqlite3.Connection, str]]:
                return [(connection, "Msg_0123456789abcdef0123456789abcdef")]

            def _msg_row_to_dict(self, raw: sqlite3.Row) -> dict[str, object]:
                return dict(raw)

        windowed = list(reader._read_history(
            PagingDatabase(), self.settings,
            dt.datetime(2026, 6, 1, tzinfo=dt.timezone.utc),
            dt.datetime(2026, 12, 1, tzinfo=dt.timezone.utc),
            5000,
        ))
        self.assertEqual([event["sourceIdentity"]["localId"] for event in windowed], ["2", "3"])
        connection.close()

    def test_partial_history_receipt_is_reported_without_job_or_message_body(self) -> None:
        receipt = {
            "jobId": "job-offline-test",
            "read": 2,
            "matched": 0,
            "created": 1,
            "needsReview": 1,
            "ignoredUnsupported": 0,
            "failed": 0,
            "status": "Partial",
            "isLastBatch": True,
            "messageBody": "private text",
        }
        opener = RecordingOpener([FakeResponse(200, json.dumps(receipt).encode("utf-8"))])
        payload = {
            "schemaVersion": 1,
            "jobId": "job-offline-test",
            "accountExternalId": "sample-account",
            "conversationExternalId": "sample-contact",
            "requestedRange": {"fromUtc": None, "toUtc": None},
            "items": [self.make_history_event(1), self.make_history_event(2)],
            "isLastBatch": False,
        }
        stderr = io.StringIO()
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", stderr):
            reader._post_contract(
                self.settings,
                "/api/reader/history-batches",
                "HistoryBatchV1",
                payload,
            )

        summary = stderr.getvalue()
        self.assertIn('"status":"Partial"', summary)
        self.assertIn('"needsReview":1', summary)
        self.assertIn('"failed":0', summary)
        self.assertIn('"isLastBatch":true', summary)
        self.assertNotIn("job-offline-test", summary)
        self.assertNotIn("private text", summary)

    def test_http_rejection_reports_only_status_and_sanitized_code(self) -> None:
        token = "t" * 40
        response_body = json.dumps({
            "error": {
                "code": "READER_SCOPE_NOT_ALLOWED",
                "message": f"private message text; bearer {token}",
            }
        }).encode("utf-8")
        rejected = urllib.error.HTTPError(
            "http://127.0.0.1:5080/api/reader/messages",
            403,
            "forbidden",
            email.message.Message(),
            io.BytesIO(response_body),
        )
        opener = RecordingOpener([rejected])
        stderr = io.StringIO()
        with mock.patch.object(reader.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(reader.sys, "__stderr__", stderr):
            with self.assertRaises(reader.ReaderFailure) as caught:
                reader._post_contract(
                    self.settings,
                    "/api/reader/messages",
                    "RawMessageEventV1",
                    self.make_event(58),
                )
            reader._error(caught.exception.code, caught.exception.details)

        output = stderr.getvalue()
        self.assertIn('"httpStatus":403', output)
        self.assertIn('"remoteCode":"READER_SCOPE_NOT_ALLOWED"', output)
        self.assertNotIn("private message text", output)
        self.assertNotIn(token, output)


if __name__ == "__main__":
    unittest.main()
