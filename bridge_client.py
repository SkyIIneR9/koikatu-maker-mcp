"""Authenticated, one-request-per-connection client for the local Unity plugin."""
from __future__ import annotations

import json
import os
import socket
from pathlib import Path

ROOT = Path(__file__).resolve().parent
_default_runtime = Path(os.environ["KK_GAME_ROOT"]) / "MCP" / "MakerBridge" / "runtime" if os.environ.get("KK_GAME_ROOT") else ROOT / "runtime"
ENDPOINT = Path(os.environ.get("KK_MAKER_ENDPOINT", str(_default_runtime / "endpoint.json")))


class BridgeError(RuntimeError):
    pass


def call(method: str, **params):
    try:
        endpoint = json.loads(ENDPOINT.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise BridgeError("MakerBridge is not running. Start/restart Koikatu after installing the plugin.") from exc
    if not isinstance(endpoint, dict):
        raise BridgeError("Invalid bridge endpoint file")
    if endpoint.get("host") != "127.0.0.1":
        raise BridgeError("Only the local loopback bridge is supported")
    if (not isinstance(endpoint.get("token"), str) or not endpoint["token"]
            or type(endpoint.get("port")) is not int or not 1 <= endpoint["port"] <= 65535):
        raise BridgeError("Invalid bridge endpoint credentials or port")
    request = json.dumps({"token": endpoint["token"], "method": method, "params": params}, allow_nan=False).encode() + b"\n"
    if len(request) > 65536:
        raise BridgeError("Request too large")
    try:
        with socket.create_connection(("127.0.0.1", int(endpoint["port"])), timeout=5) as conn:
            conn.settimeout(45)
            conn.sendall(request)
            with conn.makefile("rb") as reader:
                raw = reader.readline(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024 or not raw.endswith(b"\n"):
            raise BridgeError("Invalid or oversized bridge response")
        response = json.loads(raw)
    except (OSError, ValueError) as exc:
        raise BridgeError(f"Cannot communicate with MakerBridge: {exc}. Do not retry a mutation blindly; inspect current state.") from exc
    if not isinstance(response, dict) or type(response.get("ok")) is not bool:
        raise BridgeError("Invalid bridge response envelope")
    if not response["ok"]:
        message = response.get("error", "Unknown bridge error")
        if response.get("backup_card"):
            message += f". Recovery card: {response['backup_card']}"
        raise BridgeError(message)
    if "result" not in response:
        raise BridgeError("Bridge response is missing its result")
    return response["result"]


if __name__ == "__main__":
    import argparse
    import sys
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("method", nargs="?", default="status")
    parser.add_argument("--params", default="{}", help="JSON object")
    parser.add_argument("--params-file", type=Path)
    args = parser.parse_args()
    params = json.loads(args.params_file.read_text(encoding="utf-8") if args.params_file else args.params)
    try:
        print(json.dumps(call(args.method, **params), ensure_ascii=False, indent=2))
    except BridgeError as exc:
        parser.exit(1, f"{exc}\n")
