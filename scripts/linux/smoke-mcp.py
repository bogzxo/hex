#!/usr/bin/env python3
"""Launches Hex on Linux with --window --mcp, drives it via --mcp-connect, and captures a screenshot."""
import argparse
import base64
import json
import os
import select
import subprocess
import sys
import time


class HexMcpSession:
    def __init__(self, hex_bin: str, workspace: str, log_path: str, extra_env: dict | None = None):
        self.hex_bin = hex_bin
        self.workspace = os.path.abspath(workspace)
        self.log_path = log_path
        self.env = os.environ.copy()
        if extra_env:
            self.env.update(extra_env)
        self.gui_log = open(log_path, "w", encoding="utf-8")
        self.gui = subprocess.Popen(
            [hex_bin, "--window", "--mcp", self.workspace],
            stdout=self.gui_log,
            stderr=subprocess.STDOUT,
            env=self.env,
        )
        self.bridge = None
        self._id = 0

    def connect(self, timeout: float = 45.0):
        deadline = time.time() + timeout
        last_err = ""
        while time.time() < deadline:
            if self.gui.poll() is not None:
                self.gui_log.flush()
                raise RuntimeError(f"Hex GUI exited prematurely with code {self.gui.returncode} (see {self.log_path})")
            proc = subprocess.Popen(
                [self.hex_bin, "--mcp-connect", "--folder", self.workspace],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
                env=self.env,
            )
            self.bridge = proc
            try:
                resp = self.initialize(timeout=10.0)
                return resp
            except Exception as e:
                last_err = str(e)
                try:
                    proc.terminate()
                    proc.wait(timeout=2)
                except Exception:
                    pass
                self.bridge = None
                time.sleep(0.5)
        raise TimeoutError(f"Could not connect to Hex MCP server within {timeout}s: {last_err}")

    def _send(self, payload: dict):
        assert self.bridge and self.bridge.stdin
        self.bridge.stdin.write(json.dumps(payload) + "\n")
        self.bridge.stdin.flush()

    def _recv_id(self, expected_id: int, timeout: float = 30.0) -> dict:
        assert self.bridge and self.bridge.stdout
        deadline = time.time() + timeout
        while time.time() < deadline:
            r, _, _ = select.select([self.bridge.stdout], [], [], max(0.1, deadline - time.time()))
            if not r:
                if self.bridge.poll() is not None:
                    err = self.bridge.stderr.read() if self.bridge.stderr else ""
                    raise RuntimeError(f"Bridge exited ({self.bridge.returncode}): {err.strip()}")
                continue
            line = self.bridge.stdout.readline()
            if not line:
                err = self.bridge.stderr.read() if self.bridge.stderr else ""
                raise RuntimeError(f"EOF from Hex MCP bridge: {err.strip()}")
            line = line.strip()
            if not line or not line.startswith("{"):
                continue
            msg = json.loads(line)
            if msg.get("id") == expected_id:
                return msg
        raise TimeoutError(f"Timed out waiting for MCP response id={expected_id}")

    def initialize(self, timeout: float = 20.0) -> dict:
        self._id += 1
        self._send({
            "jsonrpc": "2.0",
            "id": self._id,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "linux-vm-smoke", "version": "1.0"},
            },
        })
        resp = self._recv_id(self._id, timeout=timeout)
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return resp

    def call(self, _tool: str, timeout: float = 30.0, **kwargs) -> dict:
        self._id += 1
        self._send({
            "jsonrpc": "2.0",
            "id": self._id,
            "method": "tools/call",
            "params": {"name": _tool, "arguments": kwargs},
        })
        return self._recv_id(self._id, timeout=timeout)

    def screenshot(self, out_path: str, scale: float = 1.0) -> int:
        resp = self.call("hex_screenshot", scale=scale)
        content = resp.get("result", {}).get("content", [])
        for item in content:
            if item.get("type") == "image" and item.get("data"):
                raw = base64.b64decode(item["data"])
                with open(out_path, "wb") as f:
                    f.write(raw)
                return len(raw)
        raise RuntimeError(f"hex_screenshot returned no image: {json.dumps(resp)[:400]}")

    def text(self, resp: dict) -> str:
        content = resp.get("result", {}).get("content", [])
        return "\n".join(c.get("text", "") for c in content if c.get("type") == "text")

    def close(self):
        if self.bridge:
            try:
                if self.bridge.stdin:
                    self.bridge.stdin.close()
            except Exception:
                pass
            try:
                self.bridge.wait(timeout=3)
            except Exception:
                self.bridge.kill()
        if self.gui and self.gui.poll() is None:
            self.gui.terminate()
            try:
                self.gui.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.gui.kill()
        self.gui_log.close()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--hex", required=True)
    p.add_argument("--workspace", required=True)
    p.add_argument("--screenshot", required=True)
    p.add_argument("--log", required=True)
    args = p.parse_args()

    sess = HexMcpSession(args.hex, args.workspace, args.log)
    try:
        init = sess.connect(timeout=60.0)
        print("[smoke-mcp] Connected:", json.dumps(init.get("result", {}).get("serverInfo", {})))
        time.sleep(1.5)
        editor = sess.text(sess.call("hex_editor"))
        print("[smoke-mcp] Editor:", editor[:300])
        nbytes = sess.screenshot(args.screenshot, scale=1.0)
        if nbytes < 5000:
            raise RuntimeError(f"Screenshot too small: {nbytes} bytes")
        print(f"[smoke-mcp] Saved {args.screenshot} ({nbytes} bytes)")
        return 0
    finally:
        sess.close()


if __name__ == "__main__":
    sys.exit(main())
