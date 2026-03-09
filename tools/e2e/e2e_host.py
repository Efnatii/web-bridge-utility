from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any


NO_PROXY_OPENER = None


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class HostState:
    def __init__(
        self,
        static_root: Path,
        scenario_path: Path,
        artifacts_dir: Path,
        logs_dir: Path,
        utility_exe: Path,
        config_path: Path,
    ) -> None:
        self.static_root = static_root
        self.scenario_path = scenario_path
        self.artifacts_dir = artifacts_dir
        self.logs_dir = logs_dir
        self.utility_exe = utility_exe
        self.config_path = config_path
        self.lock = threading.Lock()
        self.started_at = utc_now()
        self.probe_hits: list[dict[str, Any]] = []
        self.checkpoints: list[dict[str, Any]] = []
        self.reports: dict[str, dict[str, Any]] = {}
        self.last_control_results: list[dict[str, Any]] = []
        self.complete = False
        self.console_log_path = logs_dir / "browser-console.jsonl"
        self.trace_log_path = logs_dir / "http-ws-trace.jsonl"
        self.host_log_path = logs_dir / "host.log"
        self.console_log_path.parent.mkdir(parents=True, exist_ok=True)
        self.trace_log_path.parent.mkdir(parents=True, exist_ok=True)
        self.host_log_path.parent.mkdir(parents=True, exist_ok=True)

    def scan_utility_log(self, contains: list[str]) -> dict[str, Any]:
        log_path = self.logs_dir / "utility.log"
        if not log_path.exists():
            return {
                "exists": False,
                "counts": {pattern: 0 for pattern in contains},
                "matches": {pattern: [] for pattern in contains},
            }

        text = log_path.read_text(encoding="utf-8", errors="replace")
        lines = text.splitlines()
        counts: dict[str, int] = {}
        matches: dict[str, list[str]] = {}
        for pattern in contains:
            matched = [line for line in lines if pattern in line]
            counts[pattern] = len(matched)
            matches[pattern] = matched[-10:]

        return {
            "exists": True,
            "counts": counts,
            "matches": matches,
        }

    def load_scenario(self) -> dict[str, Any]:
        return json.loads(self.scenario_path.read_text(encoding="utf-8"))

    def write_json(self, path: Path, payload: Any) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    def append_jsonl(self, path: Path, payload: Any) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(payload, ensure_ascii=False))
            stream.write("\n")

    def log(self, message: str, **details: Any) -> None:
        entry = {
            "ts": utc_now(),
            "message": message,
            "details": details,
        }
        self.append_jsonl(self.host_log_path, entry)

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {
                "startedAtUtc": self.started_at,
                "probeHits": len(self.probe_hits),
                "lastProbe": self.probe_hits[-1] if self.probe_hits else None,
                "checkpoints": self.checkpoints,
                "reports": {name: {"success": report.get("success"), "ts": report.get("completedAtUtc")} for name, report in self.reports.items()},
                "complete": self.complete,
                "lastControls": self.last_control_results[-15:],
            }

    def record_probe(self, payload: dict[str, Any]) -> None:
        with self.lock:
            self.probe_hits.append(payload)
        self.log("probe", payload=payload)

    def record_checkpoint(self, payload: dict[str, Any]) -> None:
        payload.setdefault("ts", utc_now())
        with self.lock:
            self.checkpoints.append(payload)
        self.log("checkpoint", payload=payload)

    def record_report(self, name: str, payload: dict[str, Any]) -> None:
        with self.lock:
            self.reports[name] = payload
            if "main" in self.reports and "chaos" in self.reports:
                self.complete = True
        self.write_json(self.artifacts_dir / f"report-{name}.json", payload)
        self.log("report", name=name, success=payload.get("success"))

    def record_control(self, action: str, payload: dict[str, Any]) -> None:
        entry = {
            "ts": utc_now(),
            "action": action,
            "payload": payload,
        }
        with self.lock:
            self.last_control_results.append(entry)
        self.log("control", action=action, payload=payload)


def kill_processes(names: list[str]) -> dict[str, Any]:
    killed: list[dict[str, Any]] = []
    command = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        "$names = @(" + ",".join(f"'{name}'" for name in names) + "); "
        "Get-Process | Where-Object { $names -contains $_.ProcessName } | "
        "ForEach-Object { Stop-Process -Id $_.Id -Force -PassThru | Select-Object ProcessName,Id } | ConvertTo-Json -Depth 4",
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    if completed.stdout.strip():
        try:
            parsed = json.loads(completed.stdout)
            if isinstance(parsed, list):
                killed = parsed
            elif isinstance(parsed, dict):
                killed = [parsed]
        except json.JSONDecodeError:
            killed = [{"raw": completed.stdout.strip()}]
    return {
        "returncode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "killed": killed,
    }


class Handler(SimpleHTTPRequestHandler):
    state: HostState

    def log_message(self, format: str, *args: Any) -> None:
        self.state.log("http", client=self.address_string(), request=format % args)

    def translate_path(self, path: str) -> str:
        path = path.split("?", 1)[0].split("#", 1)[0]
        relative = path.lstrip("/") or "index.html"
        return str((self.state.static_root / relative).resolve())

    def do_HEAD(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/test/ping":
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.end_headers()
            return
        super().do_HEAD()

    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/api/scenario":
            return self._write_json(self.state.load_scenario())
        if parsed.path == "/api/status":
            return self._write_json(self.state.status())
        if parsed.path == "/test/ping":
            return self._write_text("pong")
        if parsed.path == "/test/json":
            return self._write_json({"status": "ok", "ts": utc_now(), "path": parsed.path})
        return super().do_GET()

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        payload = self._read_json()

        if parsed.path == "/api/probe":
            self.state.record_probe(payload)
            return self._write_json({"accepted": True})

        if parsed.path == "/api/checkpoint":
            self.state.record_checkpoint(payload)
            return self._write_json({"accepted": True})

        if parsed.path == "/api/console":
            self.state.append_jsonl(self.state.console_log_path, payload)
            return self._write_json({"accepted": True})

        if parsed.path == "/api/trace":
            self.state.append_jsonl(self.state.trace_log_path, payload)
            return self._write_json({"accepted": True})

        if parsed.path.startswith("/api/report/"):
            name = parsed.path.rsplit("/", 1)[-1]
            self.state.record_report(name, payload)
            return self._write_json({"accepted": True, "name": name})

        if parsed.path == "/api/control/spawn-second-instance":
            process = subprocess.run(
                [str(self.state.utility_exe), "--config", str(self.state.config_path)],
                capture_output=True,
                text=True,
                check=False,
                cwd=self.state.utility_exe.parent)
            result = {
                "returncode": process.returncode,
                "stdout": process.stdout,
                "stderr": process.stderr,
            }
            self.state.record_control("spawn-second-instance", result)
            return self._write_json(result)

        if parsed.path == "/api/control/cli-shutdown":
            process = subprocess.run(
                [str(self.state.utility_exe), "--shutdown"],
                capture_output=True,
                text=True,
                check=False,
                cwd=self.state.utility_exe.parent)
            result = {
                "returncode": process.returncode,
                "stdout": process.stdout,
                "stderr": process.stderr,
            }
            self.state.record_control("cli-shutdown", result)
            return self._write_json(result)

        if parsed.path == "/api/control/kill-excel":
            result = kill_processes(["EXCEL"])
            self.state.record_control("kill-excel", result)
            return self._write_json(result)

        if parsed.path == "/api/control/kill-kompas":
            result = kill_processes(["KOMPAS", "kStudy"])
            self.state.record_control("kill-kompas", result)
            return self._write_json(result)

        if parsed.path == "/api/control/http-request":
            request_url = str(payload["url"])
            method = str(payload.get("method", "GET")).upper()
            headers = payload.get("headers", {})
            body = payload.get("body")
            result = self._http_request(method, request_url, headers, body)
            self.state.record_control("http-request", {"url": request_url, "method": method, "result": result})
            return self._write_json(result, status=result.get("statusCode", 200))

        if parsed.path == "/api/control/utility-log-scan":
            contains = payload.get("contains", [])
            if not isinstance(contains, list):
                return self._write_json({"error": "invalid-contains"}, status=400)
            result = self.state.scan_utility_log([str(item) for item in contains])
            self.state.record_control("utility-log-scan", result)
            return self._write_json(result)

        return self._write_json({"error": "not-found", "path": parsed.path}, status=404)

    def _http_request(self, method: str, url: str, headers: dict[str, Any], body: Any) -> dict[str, Any]:
        import urllib.error
        import urllib.request
        global NO_PROXY_OPENER
        if NO_PROXY_OPENER is None:
            NO_PROXY_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        data = None
        if body is not None:
            if isinstance(body, (dict, list)):
                data = json.dumps(body).encode("utf-8")
            elif isinstance(body, str):
                data = body.encode("utf-8")
            else:
                data = json.dumps(body).encode("utf-8")

        request = urllib.request.Request(url=url, method=method, data=data)
        for key, value in headers.items():
            request.add_header(key, str(value))
        try:
            with NO_PROXY_OPENER.open(request, timeout=30) as response:
                raw = response.read()
                content_type = response.headers.get_content_type()
                content = raw.decode("utf-8", errors="replace")
                parsed: Any = content
                if "json" in content_type:
                    try:
                        parsed = json.loads(content)
                    except json.JSONDecodeError:
                        parsed = content
                return {
                    "statusCode": response.status,
                    "headers": dict(response.headers.items()),
                    "content": parsed,
                }
        except urllib.error.HTTPError as exception:  # pragma: no cover - external I/O path
            raw = exception.read()
            content = raw.decode("utf-8", errors="replace")
            parsed: Any = content
            if "json" in (exception.headers.get_content_type() or ""):
                try:
                    parsed = json.loads(content)
                except json.JSONDecodeError:
                    parsed = content
            return {
                "statusCode": exception.code,
                "headers": dict(exception.headers.items()),
                "content": parsed,
                "error": str(exception),
            }
        except Exception as exception:  # pragma: no cover - external I/O path
            return {
                "statusCode": 599,
                "error": str(exception),
            }

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length > 0 else b"{}"
        if not raw:
            return {}
        return json.loads(raw.decode("utf-8"))

    def _write_text(self, text: str, status: int = 200) -> None:
        encoded = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def _write_json(self, payload: Any, status: int = 200) -> None:
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5510)
    parser.add_argument("--static-root", required=True)
    parser.add_argument("--scenario", required=True)
    parser.add_argument("--artifacts-dir", required=True)
    parser.add_argument("--logs-dir", required=True)
    parser.add_argument("--utility-exe", required=True)
    parser.add_argument("--config-path", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    static_root = Path(args.static_root).resolve()
    scenario_path = Path(args.scenario).resolve()
    artifacts_dir = Path(args.artifacts_dir).resolve()
    logs_dir = Path(args.logs_dir).resolve()
    utility_exe = Path(args.utility_exe).resolve()
    config_path = Path(args.config_path).resolve()

    state = HostState(static_root, scenario_path, artifacts_dir, logs_dir, utility_exe, config_path)
    Handler.state = state
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    state.log("host-started", host=args.host, port=args.port, staticRoot=str(static_root))
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        state.log("host-stopped", reason="keyboard-interrupt")
        return 0
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
