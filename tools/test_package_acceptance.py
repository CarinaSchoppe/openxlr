"""Offline contracts for the installed-package acceptance driver."""
import json
from pathlib import Path
import runpy
import unittest
from unittest.mock import Mock, patch
from pipewire_snapshot import parse_dump

installed = runpy.run_path(str(Path(__file__).with_name("verify-installed.py")))
command = installed["command"]
wait_for_ui_window = installed["wait_for_ui_window"]
wait_application_sink = runpy.run_path(str(Path(__file__).with_name("verify-live-mixer.py")))["wait_application_sink"]
capture_with_minimum_samples = runpy.run_path(str(Path(__file__).with_name("verify-native-host.py")))["capture_with_minimum_samples"]
pulse_endpoint_exists = runpy.run_path(str(Path(__file__).with_name("verify-native-host.py")))["pulse_endpoint_exists"]
pulse_process_exists = runpy.run_path(str(Path(__file__).with_name("verify-native-host.py")))["pulse_process_exists"]


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


class NativeCaptureStartupTests(unittest.TestCase):
    def test_short_startup_captures_are_retried(self):
        capture = Mock(side_effect=[[0], [0] * 48001])
        with patch("time.sleep"):
            result = capture_with_minimum_samples(capture)
        self.assertEqual(48001, len(result))
        self.assertEqual(2, capture.call_count)

    def test_repeated_short_captures_still_fail(self):
        with patch("time.sleep"), self.assertRaisesRegex(AssertionError, r"\[1, 2, 3\] samples"):
            capture_with_minimum_samples(Mock(side_effect=[[0], [0, 0], [0, 0, 0]]))

    def test_pulse_endpoint_names_match_exactly(self):
        listing = "42\tqa_out.monitor\tmodule-protocol-pulse.c\tfloat32le 2ch 48000Hz\n"
        self.assertTrue(pulse_endpoint_exists(listing, "qa_out.monitor"))
        self.assertFalse(pulse_endpoint_exists(listing, "qa_out"))
        self.assertFalse(pulse_endpoint_exists("malformed\n", "qa_out.monitor"))

    def test_pulse_stream_matches_its_process(self):
        listing = '\t\tapplication.process.id = "4321"\n\t\tnode.name = "qa_capture"\n'
        self.assertTrue(pulse_process_exists(listing, 4321))
        self.assertFalse(pulse_process_exists(listing, 432))


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


class InstalledUiStartupTests(unittest.TestCase):
    def test_waits_for_the_real_window_instead_of_an_idle_process(self):
        audio = Mock()
        audio.run.side_effect = ["unnamed helper windows", '0x1 "OpenXLR"']
        desktop = Mock(returncode=None)
        desktop.poll.return_value = None
        with patch("time.monotonic", side_effect=[0, 0, 0]), patch("time.sleep"):
            wait_for_ui_window(audio, desktop)
        self.assertEqual(2, audio.run.call_count)

    def test_reports_an_early_ui_exit(self):
        desktop = Mock(returncode=17)
        desktop.poll.return_value = 17
        with patch("time.monotonic", return_value=0), self.assertRaisesRegex(
                AssertionError, "installed UI exited 17"):
            wait_for_ui_window(Mock(), desktop)

    def test_timeout_includes_the_last_window_tree(self):
        audio = Mock()
        audio.run.return_value = "last X11 tree"
        desktop = Mock(returncode=None)
        desktop.poll.return_value = None
        with patch("time.monotonic", side_effect=[0, 0, 31]), patch("time.sleep"), \
                self.assertRaisesRegex(AssertionError, "last X11 tree"):
            wait_for_ui_window(audio, desktop)


if __name__ == "__main__":
    unittest.main()
