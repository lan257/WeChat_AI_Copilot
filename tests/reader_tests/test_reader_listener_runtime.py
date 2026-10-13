"""Adapter-level contract check for the pinned Listener without WeChat data."""

from __future__ import annotations

import importlib
import sys
import threading
import unittest
from pathlib import Path


PRODUCT_ROOT = Path(__file__).resolve().parents[2]
RUNTIME_ROOT = PRODUCT_ROOT / "src" / "WeChatAiCopilot.Reader" / ".runtime"
PACKAGE_ROOT = RUNTIME_ROOT / "package"
DEPENDENCY_ROOT = RUNTIME_ROOT / "deps"


@unittest.skipUnless((PACKAGE_ROOT / "wechatauto" / "db.py").is_file(), "run prepare-reader.ps1 to prepare the pinned Reader runtime")
class PinnedListenerTests(unittest.TestCase):
    def test_one_listener_dispatches_independent_callbacks_for_two_targets(self) -> None:
        sys.path.insert(0, str(PACKAGE_ROOT))
        sys.path.insert(0, str(DEPENDENCY_ROOT))
        module = importlib.import_module("wechatauto.db")

        class EmptyDatabase:
            def get_messages(self, _user: str, limit: int = 1) -> list[dict]:
                return []

        first = threading.Event()
        second = threading.Event()
        listener = module.Listener(EmptyDatabase(), interval=0.01, watermark_file="", max_retries=0)
        listener.add_listener("target-a", lambda _row, _listener: first.set())
        listener.add_listener("target-b", lambda _row, _listener: second.set())
        try:
            listener._dispatch("target-a", [{"sort_seq": 1}])
            listener._dispatch("target-b", [{"sort_seq": 1}])
            self.assertTrue(first.wait(1))
            self.assertTrue(second.wait(1))
            self.assertEqual(set(listener._callbacks), {"target-a", "target-b"})
        finally:
            listener.stop()
            sys.path.remove(str(DEPENDENCY_ROOT))
            sys.path.remove(str(PACKAGE_ROOT))
            sys.modules.pop("wechatauto.db", None)
            sys.modules.pop("wechatauto", None)
