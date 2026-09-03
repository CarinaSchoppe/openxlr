"""Offline contracts for the installed-package acceptance driver."""
import json
from pathlib import Path
import runpy
import unittest
from unittest.mock import Mock

command = runpy.run_path(str(Path(__file__).with_name("verify-installed.py")))["command"]


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
