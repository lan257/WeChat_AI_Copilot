"""Offline tests for the formal M1 Reader adapter."""

from __future__ import annotations

import datetime as dt
import email.message
import importlib.util
import io
import json
import os
import sys
import tempfile
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


class ReaderConfigTests(unittest.TestCase):
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


class ReaderContractTests(unittest.TestCase):
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
