#!/usr/bin/env python3
"""Regression fixtures for performance evidence completeness and statistics."""
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("capture", Path(__file__).with_name("summarize-performance-capture.py"))
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class CaptureTests(unittest.TestCase):
    def summarize(self, events, footer=True, dropped=0):
        rows = [{"kind": "capture", "schema": 1, "provider": "XcpNgCenter-Shell-Performance"}]
        rows += [{"kind": "event", "elapsedMilliseconds": index, "eventId": event, "payload": payload}
                 for index, (event, payload) in enumerate(events)]
        if footer:
            rows.append({"kind": "summary", "eventsWritten": len(events), "eventsDropped": dropped,
                         "durationMilliseconds": 10000})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "capture.jsonl")
            path.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            return capture.summarize(path, "synthetic")

    def test_statistics_and_unknown_allocations(self):
        report = self.summarize([(1, ["inventory.refresh", value, -1 if value == 100 else 1024])
                                 for value in [1, 2, 3, 4, 100]])
        operation = report["operations"]["inventory.refresh"]
        self.assertEqual((3, 100, 100, 4), (operation["medianMs"], operation["p95Ms"],
                                          operation["p99Ms"], operation["knownAllocationSamples"]))
        self.assertEqual("synthetic", report["evidenceKind"])

    def test_phase_coverage_and_rates(self):
        report = self.summarize([(1, ["inventory.refresh", 1, 1]), (2, [200, 2]),
                                 (1, ["details.refresh", 2, 2]), (1, ["graphs.rrd-fetch", 3, 3]),
                                 (4, ["handshake", "connected"]), (3, [1024, 768])])
        self.assertTrue(all(report["coverage"].values()))
        self.assertEqual(200, report["inventory"]["notificationsPerBatch"])
        self.assertEqual(.1, report["console"]["framesPerSecond"])
        self.assertEqual([], report["pendingPhases"])

    def test_missing_footer_is_incomplete(self):
        report = self.summarize([(1, ["details.refresh", 1, 1])], footer=False)
        self.assertFalse(report["complete"])
        self.assertFalse(report["usableForComparison"])
        self.assertIn("inventory", report["pendingPhases"])

    def test_dropped_samples_cannot_establish_comparison(self):
        self.assertFalse(self.summarize([(3, [10, 10])], dropped=1)["usableForComparison"])

    def test_empty_capture_cannot_establish_comparison(self):
        self.assertFalse(self.summarize([])["usableForComparison"])

    def test_invalid_event_and_nonfinite_samples_rejected(self):
        for event in [(99, []), (1, ["details.refresh", float("nan"), 1]), (3, [-1, 3])]:
            with self.assertRaises(ValueError):
                self.summarize([event])


if __name__ == "__main__":
    unittest.main()
