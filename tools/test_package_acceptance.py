"""Offline contracts for the installed-package acceptance driver."""
import json
from pathlib import Path
import runpy
import unittest
from unittest.mock import Mock, patch
from pipewire_snapshot import parse_dump

command = runpy.run_path(str(Path(__file__).with_name("verify-installed.py")))["command"]
wait_application_sink = runpy.run_path(str(Path(__file__).with_name("verify-live-mixer.py")))["wait_application_sink"]


class RoutePublicationTests(unittest.TestCase):
    def test_waits_for_actual_destination_after_acknowledgement(self):
        check = Mock(side_effect=[AssertionError("old sink"), None])
        with patch.dict(wait_application_sink.__globals__, assert_application_sink=check), \
             patch("time.monotonic", return_value=0), patch("time.sleep"):
            wait_application_sink("player", "music")
        self.assertEqual(2, check.call_count)
        check.assert_called_with("player", "music")

    def test_wrong_destination_still_fails_within_the_deadline(self):
        check = Mock(side_effect=AssertionError("wrong sink"))
        with patch.dict(wait_application_sink.__globals__, assert_application_sink=check), \
             patch("time.monotonic", side_effect=[0, 6]), self.assertRaisesRegex(AssertionError, "wrong sink"):
            wait_application_sink("player", "music")


class PipeWireSnapshotTests(unittest.TestCase):
    def test_single_snapshot_preserves_duplicate_names_for_detection(self):
        snapshot = [{"id": 1, "name": "same"}, {"id": 2, "name": "same"}]
        self.assertEqual(snapshot, parse_dump(json.dumps(snapshot)))

    def test_change_batches_replace_and_remove_by_id(self):
        stream = '[{"id":1,"name":"old"},{"id":2},{"id":3}]\n' \
            '[{"id":1,"name":"new"},{"id":2,"info":null},{"id":3,"props":null}]\n[{"id":4}]'
        self.assertEqual([{"id": 1, "name": "new"}, {"id": 4}], parse_dump(stream))

    def test_malformed_streams_fail_instead_of_discarding_trailing_data(self):
        for stream in ('', '{}', '[] garbage', '[] {}', '[] [{"id":"wrong"}]',
                       '[] [{"id":-1}]', '[] [{"id":true}]', '[] [{"id":4294967296}]'):
            with self.subTest(stream=stream), self.assertRaises(ValueError):
                parse_dump(stream)


class PackageCommandTests(unittest.TestCase):
    def connection(self, error=None):
        connection = Mock()
        def receive():
            request = json.loads(connection.send.call_args.args[0])
            return json.dumps(dict(type="commandResult", requestId=request["requestId"], error=error))
        connection.recv.side_effect = receive
        return connection

    def test_layout_name_is_a_payload_not_the_command_argument(self):
        connection = self.connection()
        command(connection, "createChannel", name='Music "QA"')
        request = json.loads(connection.send.call_args.args[0])
        self.assertEqual("createChannel", request["cmd"])
        self.assertEqual('Music "QA"', request["name"])

    def test_daemon_errors_fail_acceptance(self):
        with self.assertRaises(AssertionError):
            command(self.connection("graph unavailable"), "deleteMix", mix="qa")


if __name__ == "__main__":
    unittest.main()
