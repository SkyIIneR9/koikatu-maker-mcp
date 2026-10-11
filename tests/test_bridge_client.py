"""Transport contract tests using a local fake plugin; no game or MCP SDK needed."""
import json
import socket
import sys
import tempfile
import threading
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bridge_client as bridge


class TransportTests(unittest.TestCase):
    @contextmanager
    def endpoint(self, **overrides):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'endpoint.json'
            data = {'host': '127.0.0.1', 'port': 1, 'token': 'test-token'}
            data.update(overrides)
            path.write_text(json.dumps(data), encoding='utf-8-sig')
            with patch.object(bridge, 'ENDPOINT', path):
                yield data

    def exchange(self, reply, *, token='test-token'):
        seen, errors = [], []
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen(1)
            listener.settimeout(3)

            def serve():
                try:
                    with listener.accept()[0] as conn:
                        conn.settimeout(3)
                        with conn.makefile('rb') as stream:
                            seen.append(json.loads(stream.readline()))
                        wire = reply if isinstance(reply, bytes) else json.dumps(reply).encode() + b'\n'
                        if seen[-1]['token'] != 'test-token':
                            wire = b'{"ok":false,"error":"Unauthorized"}\n'
                        try:
                            conn.sendall(wire)
                        except (BrokenPipeError, ConnectionResetError):
                            pass  # Client intentionally rejects an oversized reply.
                except Exception as exc:
                    errors.append(exc)

            thread = threading.Thread(target=serve, daemon=True)
            thread.start()
            try:
                with self.endpoint(port=listener.getsockname()[1], token=token):
                    result = bridge.call('get_appearance', slot='front')
            finally:
                thread.join(4)
                self.assertFalse(thread.is_alive())
                self.assertEqual(errors, [])
            return result, seen

    def test_authenticated_request_and_unicode_response(self):
        result, seen = self.exchange({'ok': True, 'result': {'name': '髪'}})
        self.assertEqual(result, {'name': '髪'})
        self.assertEqual(len(seen[0].pop('request_id')), 32)
        self.assertEqual(seen, [{'token': 'test-token', 'method': 'get_appearance', 'params': {'slot': 'front'}}])

    def test_bad_token_surfaces_rejection(self):
        with self.assertRaisesRegex(bridge.BridgeError, 'Unauthorized'):
            self.exchange({'ok': True, 'result': {}}, token='wrong-token')

    def test_mutation_failure_preserves_recovery_path(self):
        with self.assertRaisesRegex(bridge.BridgeError, 'Recovery card: male/MCP/_backups/test.png'):
            self.exchange({'ok': False, 'error': 'Reload failed', 'backup_card': 'male/MCP/_backups/test.png'})

    def test_uncertain_response_exposes_operation_id(self):
        with self.assertRaisesRegex(bridge.BridgeError, r'Operation ID: [0-9a-f]{32}'):
            self.exchange(b'{"ok":true')

    def test_invalid_response_envelopes(self):
        for reply in [b'not-json\n', [], {'ok': 'true'}, {'ok': True}, b'{}']:
            with self.subTest(reply=reply), self.assertRaises(bridge.BridgeError):
                self.exchange(reply)

    def test_oversized_reply(self):
        with self.assertRaisesRegex(bridge.BridgeError, 'oversized'):
            self.exchange(b'x' * (8 * 1024 * 1024 + 1) + b'\n')

    def test_bad_endpoints_never_connect(self):
        for args in [{'host': 'example.com'}, {'token': ''}, {'port': 70000}, {'port': '8766'}]:
            with self.subTest(args=args), self.endpoint(**args), patch.object(socket, 'create_connection') as connect:
                with self.assertRaises(bridge.BridgeError):
                    bridge.call('status')
                connect.assert_not_called()

    def test_missing_endpoint(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(bridge, 'ENDPOINT', Path(temp) / 'missing.json'):
            with self.assertRaisesRegex(bridge.BridgeError, 'not running'):
                bridge.call('status')

    def test_oversized_request_never_connects(self):
        with self.endpoint(), patch.object(socket, 'create_connection') as connect:
            with self.assertRaisesRegex(bridge.BridgeError, 'Request too large'):
                bridge.call('set_hair', junk='x' * 65536)
            connect.assert_not_called()


if __name__ == '__main__':
    unittest.main()
