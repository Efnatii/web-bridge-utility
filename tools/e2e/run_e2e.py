from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[2]
TOOLS_DIR = Path(__file__).resolve().parent
RUNNER_DIR = TOOLS_DIR / "runner"
CATALOGS_DIR = TOOLS_DIR / "catalogs"
NODE_RUNTIME_DIR = TOOLS_DIR
PROJECT_PATH = REPO_ROOT / "src" / "WebBridge.Utility" / "WebBridge.Utility.csproj"
PUBLISH_DIR = REPO_ROOT / "artifacts" / "publish" / "utility" / "win-x64"
PUBLISHED_EXE = PUBLISH_DIR / "WebBridge.Utility.exe"
CONFIG_TEMPLATE = REPO_ROOT / "configs" / "config.development.sample.json"
NO_PROXY_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def read_json_lines(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []

    items: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            payload = json.loads(line)
        except json.JSONDecodeError:
            payload = {"raw": line}
        items.append(payload)
    return items


def run(command: list[str], *, cwd: Path | None = None, timeout: int = 0, capture: bool = True) -> subprocess.CompletedProcess[str]:
    print(f"$ {' '.join(command)}", flush=True)
    return subprocess.run(
        command,
        cwd=str(cwd) if cwd else None,
        capture_output=capture,
        text=True,
        timeout=timeout if timeout > 0 else None,
        check=False)


def ps(command: str) -> subprocess.CompletedProcess[str]:
    return run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
        cwd=REPO_ROOT,
        timeout=120)


def ensure_publish() -> None:
    build = run(["dotnet", "build", str(REPO_ROOT / "WebBridge.Utility.sln")], cwd=REPO_ROOT, timeout=1800)
    if build.returncode != 0:
        raise RuntimeError(f"dotnet build failed\nSTDOUT:\n{build.stdout}\nSTDERR:\n{build.stderr}")

    publish = run(
        [
            "dotnet",
            "publish",
            str(PROJECT_PATH),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "-p:PublishSingleFile=true",
            "-p:SelfContained=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-o",
            str(PUBLISH_DIR),
        ],
        cwd=REPO_ROOT,
        timeout=2400)
    if publish.returncode != 0:
        raise RuntimeError(f"dotnet publish failed\nSTDOUT:\n{publish.stdout}\nSTDERR:\n{publish.stderr}")

    if not PUBLISHED_EXE.exists():
        raise RuntimeError(f"Published exe was not found: {PUBLISHED_EXE}")


def ensure_node_runtime() -> None:
    npm_executable = "npm.cmd" if os.name == "nt" else "npm"
    node_modules = NODE_RUNTIME_DIR / "node_modules"
    if node_modules.exists():
        return

    install = run(
        [npm_executable, "install", "--no-fund", "--no-audit"],
        cwd=NODE_RUNTIME_DIR,
        timeout=2400)
    if install.returncode != 0:
        raise RuntimeError(f"npm install failed\nSTDOUT:\n{install.stdout}\nSTDERR:\n{install.stderr}")


def pick_browser() -> str:
    edge = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
    chrome = Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")
    if edge.exists():
        return "msedge"
    if chrome.exists():
        return "chrome"
    return "chromium"


def wait_http_json(url: str, timeout_seconds: int = 60) -> Any:
    deadline = time.time() + timeout_seconds
    last_error: Exception | None = None
    while time.time() < deadline:
        try:
            with NO_PROXY_OPENER.open(url, timeout=5) as response:
                return json.loads(response.read().decode("utf-8"))
        except Exception as exc:
            last_error = exc
            time.sleep(1.0)
    raise RuntimeError(f"Timed out waiting for {url}: {last_error}")


def wait_health(base_url: str, timeout_seconds: int = 60) -> None:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        try:
            with NO_PROXY_OPENER.open(f"{base_url}/health", timeout=5) as response:
                if response.status == 200:
                    return
        except Exception:
            time.sleep(1.0)
    raise RuntimeError("Utility did not become healthy in time.")


def enum_values(assembly_path: str, enum_name: str) -> dict[str, int]:
    script = textwrap.dedent(
        f"""
        $asm = [System.Reflection.Assembly]::LoadFrom('{assembly_path}')
        $type = $asm.GetTypes() | Where-Object Name -eq '{enum_name}' | Select-Object -First 1
        if (-not $type) {{ throw 'Enum {enum_name} not found' }}
        $pairs = [ordered]@{{}}
        foreach ($name in [Enum]::GetNames($type)) {{
          $pairs[$name] = [int][Enum]::Parse($type, $name)
        }}
        $pairs | ConvertTo-Json -Depth 5
        """
    ).strip()
    completed = ps(script)
    if completed.returncode != 0:
        raise RuntimeError(f"Failed to read enum {enum_name}: {completed.stderr}")
    parsed = json.loads(completed.stdout)
    return {str(key): int(value) for key, value in parsed.items()}


def find_kompas_sample() -> Path | None:
    candidates = [
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\Cable3D\Plug.frw"),
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\Cable3D\Point.frw"),
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\Cable3D\Socket.frw"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def unique_paths(paths: list[Path]) -> list[Path]:
    seen: set[str] = set()
    unique: list[Path] = []
    for path in paths:
        rendered = str(path).lower()
        if rendered in seen:
            continue
        seen.add(rendered)
        unique.append(path)
    return unique


def resolve_configured_path(raw_path: str) -> Path:
    path = Path(raw_path)
    if path.is_absolute():
        return path
    return (CONFIG_TEMPLATE.parent / path).resolve()


def find_kompas_api7_interop(template_config: dict[str, Any]) -> tuple[Path | None, list[str]]:
    configured_paths: list[Path] = []
    adapters = template_config.get("Adapters", {}).get("Com", [])
    for adapter in adapters:
        if str(adapter.get("AdapterName", "")).lower() != "kompas":
            continue
        configured_paths.extend(
            resolve_configured_path(str(path))
            for path in adapter.get("InteropAssemblies", [])
            if str(path).strip())
        break

    candidates = unique_paths(configured_paths + [
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll"),
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Bin\Interop.KompasAPI7.dll"),
    ])
    for candidate in candidates:
        if candidate.exists():
            return candidate, [str(path) for path in candidates]
    return None, [str(path) for path in candidates]


def find_kompas_table_template() -> tuple[Path | None, list[str]]:
    candidates = unique_paths([
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Tutorials\Приемы работы в КОМПАС-График\5 Оформления документов\Результат\Stamp.tbl"),
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\Floorplan\Sys\Asar\ScheduleTemplate.tbl"),
        Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\ServiceTools\Komlib.tbl"),
    ])

    install_root = Path(r"C:\Program Files\ASCON\KOMPAS-3D v24")
    if install_root.exists():
        discovered = sorted(install_root.rglob("*.tbl"), key=lambda path: str(path).lower())
        candidates = unique_paths(candidates + discovered)

    for candidate in candidates:
        if candidate.exists():
            return candidate, [str(path) for path in candidates]
    return None, [str(path) for path in candidates]


def patch_kompas_adapter_config(config: dict[str, Any], kompas_api7_interop: Path | None) -> None:
    adapters = config.setdefault("Adapters", {}).setdefault("Com", [])
    for adapter in adapters:
        if str(adapter.get("AdapterName", "")).lower() != "kompas":
            continue
        adapter["InteropAssemblies"] = [str(kompas_api7_interop.resolve())] if kompas_api7_interop else []
        return


def arg(name: str, converter: str | None = None, *, argument_name: str | None = None, by_ref: bool = False, capture_as: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"FromArgument": name}
    if converter:
        payload["Converter"] = converter
    if argument_name:
        payload["Name"] = argument_name
    if by_ref:
        payload["ByRef"] = True
    if capture_as:
        payload["CaptureAs"] = capture_as
    return payload


def literal(value: Any, converter: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"Literal": value}
    if converter:
        payload["Converter"] = converter
    return payload


def step(
    operation: str,
    member: str = "",
    *,
    args: list[dict[str, Any]] | None = None,
    value_argument: str | None = None,
    store_as: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"Operation": operation, "Member": member}
    if args:
        payload["Args"] = args
    if value_argument:
        payload["ValueArgument"] = value_argument
    if store_as:
        payload["StoreAs"] = store_as
    return payload


def command(
    adapter: str,
    root: str,
    chain: list[dict[str, Any]],
    *,
    default_arguments: dict[str, Any] | None = None,
    return_path: str | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "Adapter": adapter,
        "Invoke": {
            "Root": root,
            "Chain": chain,
        },
    }
    if return_path:
        payload["Invoke"]["ReturnPath"] = return_path
    if default_arguments:
        payload["DefaultArguments"] = default_arguments
    return payload


def build_system_commands() -> dict[str, Any]:
    return {
        "system.file.write-text": command("system", "type:System.IO.File", [
            step("call", "WriteAllText", args=[arg("path", "path"), arg("contents", "string")]),
            step("call", "ReadAllText", args=[arg("path", "path")]),
        ]),
        "system.file.append-text": command("system", "type:System.IO.File", [
            step("call", "AppendAllText", args=[arg("path", "path"), arg("contents", "string")]),
            step("call", "ReadAllText", args=[arg("path", "path")]),
        ]),
        "system.file.read-text": command("system", "type:System.IO.File", [step("call", "ReadAllText", args=[arg("path", "path")])]),
        "system.generic.file.read-text-static": command("system", "type:System.IO.File", [step("call", "ReadAllText", args=[arg("path", "path")])]),
        "system.file.write-bytes": command("system", "type:System.IO.File", [
            step("call", "WriteAllBytes", args=[arg("path", "path"), arg("bytes", "bytes")]),
            step("call", "ReadAllBytes", args=[arg("path", "path")]),
        ]),
        "system.file.read-bytes": command("system", "type:System.IO.File", [step("call", "ReadAllBytes", args=[arg("path", "path")])]),
        "system.file.copy": command("system", "type:System.IO.File", [
            step("call", "Copy", args=[arg("source", "path"), arg("destination", "path")]),
            step("call", "ReadAllText", args=[arg("destination", "path")]),
        ]),
        "system.file.move": command("system", "type:System.IO.File", [
            step("call", "Move", args=[arg("source", "path"), arg("destination", "path")]),
            step("call", "ReadAllText", args=[arg("destination", "path")]),
        ]),
        "system.file.replace": command("system", "type:System.IO.File", [
            step("call", "Replace", args=[arg("sourceFileName", "path"), arg("destinationFileName", "path"), arg("backupFileName", "path")]),
            step("call", "ReadAllText", args=[arg("destinationFileName", "path")]),
        ]),
        "system.file.info": command("system", "type:System.IO.FileInfo", [step("new", "", args=[arg("path", "path")])]),
        "system.generic.file-info.length": command("system", "type:System.IO.FileInfo", [step("new", "", args=[arg("path", "path")]), step("get", "Length")]),
        "system.file.exists": command("system", "type:System.IO.File", [step("call", "Exists", args=[arg("path", "path")])]),
        "system.file.delete": command("system", "type:System.IO.File", [
            step("call", "Delete", args=[arg("path", "path")]),
            step("call", "Exists", args=[arg("path", "path")]),
        ]),
        "system.directory.exists": command("system", "type:System.IO.Directory", [step("call", "Exists", args=[arg("path", "path")])]),
        "system.directory.create": command("system", "type:System.IO.Directory", [step("call", "CreateDirectory", args=[arg("path", "path")])]),
        "system.directory.list": command("system", "type:System.IO.Directory", [step("call", "GetFileSystemEntries", args=[arg("path", "path")])]),
        "system.directory.files": command("system", "type:System.IO.Directory", [step("call", "GetFiles", args=[arg("path", "path"), arg("searchPattern", "string"), arg("searchOption", "string")])]),
        "system.generic.directory-info.exists": command("system", "type:System.IO.DirectoryInfo", [step("new", "", args=[arg("path", "path")]), step("get", "Exists")]),
        "system.directory.delete": command("system", "type:System.IO.Directory", [
            step("call", "Delete", args=[arg("path", "path"), arg("recursive", "bool")]),
            step("call", "Exists", args=[arg("path", "path")]),
        ]),
        "system.path.combine": command("system", "type:System.IO.Path", [step("call", "Combine", args=[arg("parts", "stringArray")])]),
        "system.path.relative": command("system", "type:System.IO.Path", [step("call", "GetRelativePath", args=[arg("relativeTo", "path"), arg("path", "path")])]),
        "system.generic.path.extension": command("system", "type:System.IO.Path", [step("call", "GetExtension", args=[arg("path", "string")])]),
        "system.environment.expand": command("system", "type:System.Environment", [step("call", "ExpandEnvironmentVariables", args=[arg("value", "string")])]),
        "system.generic.environment.machine-name": command("system", "type:System.Environment", [step("get", "MachineName")]),
        "system.process.start": command("system", "process", [step("call", "Start", args=[arg("fileName", "string"), arg("arguments", "string"), arg("workingDirectory", "path"), arg("shellExecute", "bool"), arg("createNoWindow", "bool"), arg("waitForExit", "bool"), arg("timeoutMilliseconds", "int")])]),
        "system.command.run": command(
            "system",
            "command",
            [step("call", "Run", args=[arg("fileName", "string"), arg("arguments", "string"), arg("workingDirectory", "path"), arg("timeoutMilliseconds", "int"), arg("standardInput", "string"), arg("environment", "json")])],
            default_arguments={"standardInput": None, "environment": None}),
        "system.http.get-string": command("system", "http", [step("call", "GetString", args=[arg("url", "string")])]),
        "system.http.head": command("system", "http", [step("call", "Head", args=[arg("url", "string")])]),
        "system.registry.create": command("system", "registry", [step("call", "CreateKey", args=[arg("hive", "string"), arg("keyPath", "string")])]),
        "system.registry.set": command("system", "registry", [step("call", "SetValue", args=[arg("hive", "string"), arg("keyPath", "string"), arg("valueName", "string"), arg("value", "json"), arg("valueKind", "string")])]),
        "system.registry.get": command("system", "registry", [step("call", "GetValue", args=[arg("hive", "string"), arg("keyPath", "string"), arg("valueName", "string")])]),
        "system.registry.delete-value": command("system", "registry", [step("call", "DeleteValue", args=[arg("hive", "string"), arg("keyPath", "string"), arg("valueName", "string"), arg("throwOnMissingValue", "bool")])]),
        "system.registry.delete-key": command("system", "registry", [step("call", "DeleteKey", args=[arg("hive", "string"), arg("keyPath", "string"), arg("recursive", "bool")])]),
        "system.zip.create": command("system", "zip", [step("call", "CreateFromFiles", args=[arg("archivePath", "path"), arg("files", "stringArray"), arg("baseDirectory", "path")])]),
        "system.zip.list": command("system", "zip", [step("call", "ListEntries", args=[arg("archivePath", "path")])]),
        "system.zip.extract": command("system", "zip", [step("call", "ExtractToDirectory", args=[arg("sourceArchiveFileName", "path"), arg("destinationDirectoryName", "path"), arg("overwriteFiles", "bool")])]),
        "system.hash.file": command("system", "hash", [step("call", "ComputeFile", args=[arg("algorithm", "string"), arg("path", "path")])]),
        "system.drive.list": command("system", "drive", [step("call", "List")]),
    }


def build_excel_commands() -> dict[str, Any]:
    return {
        "excel.application": command("excel", "application", []),
        "excel.application.set-visible": command("excel", "application", [step("set", "Visible", value_argument="visible")]),
        "excel.application.get-visible": command("excel", "application", [step("get", "Visible")]),
        "excel.application.get-hwnd": command("excel", "application", [step("get", "Hwnd")]),
        "excel.application.set-display-alerts": command("excel", "application", [step("set", "DisplayAlerts", value_argument="value")]),
        "excel.application.get-display-alerts": command("excel", "application", [step("get", "DisplayAlerts")]),
        "excel.application.set-screen-updating": command("excel", "application", [step("set", "ScreenUpdating", value_argument="value")]),
        "excel.application.get-screen-updating": command("excel", "application", [step("get", "ScreenUpdating")]),
        "excel.application.active-sheet-name": command("excel", "application", [step("get", "ActiveSheet"), step("get", "Name")]),
        "excel.application.active-workbook": command("excel", "application", [step("get", "ActiveWorkbook")]),
        "excel.workbooks.add": command("excel", "application", [step("get", "Workbooks"), step("call", "Add")]),
        "excel.workbook.get-name": command("excel", "handle", [step("get", "Name")]),
        "excel.workbook.get-full-name": command("excel", "handle", [step("get", "FullName")]),
        "excel.workbook.get-saved": command("excel", "handle", [step("get", "Saved")]),
        "excel.workbook.worksheet-count": command("excel", "handle", [step("get", "Worksheets"), step("get", "Count")]),
        "excel.workbook.add-worksheet": command("excel", "handle", [step("get", "Worksheets"), step("call", "Add")]),
        "excel.worksheet.rename": command("excel", "handle", [step("set", "Name", value_argument="name")]),
        "excel.worksheet.get-name": command("excel", "handle", [step("get", "Name")]),
        "excel.worksheet.activate": command("excel", "handle", [step("call", "Activate")]),
        "excel.workbook.sheet-by-name": command("excel", "handle", [step("get", "Worksheets"), step("index", "", args=[arg("sheetName", "string")])]),
        "excel.worksheet.used-range-address": command("excel", "handle", [step("get", "UsedRange"), step("get", "Address")]),
        "excel.worksheet.get-auto-filter-mode": command("excel", "handle", [step("get", "AutoFilterMode")]),
        "excel.worksheet.set-tab-color": command("excel", "handle", [step("get", "Tab"), step("set", "Color", value_argument="color")]),
        "excel.worksheet.get-tab-color": command("excel", "handle", [step("get", "Tab"), step("get", "Color")]),
        "excel.worksheet.range": command("excel", "handle", [step("call", "Range", args=[arg("address", "string")])]),
        "excel.range.set-value": command("excel", "handle", [step("set", "Value2", value_argument="value")]),
        "excel.range.set-matrix": command("excel", "handle", [step("set", "Value2", args=[arg("value", "matrix")])]),
        "excel.range.get-value": command("excel", "handle", [step("get", "Value2")]),
        "excel.range.get-text": command("excel", "handle", [step("get", "Text")]),
        "excel.range.get-address": command("excel", "handle", [step("get", "Address")]),
        "excel.range.get-columns-count": command("excel", "handle", [step("get", "Columns"), step("get", "Count")]),
        "excel.range.get-rows-count": command("excel", "handle", [step("get", "Rows"), step("get", "Count")]),
        "excel.range.get-current-region-address": command("excel", "handle", [step("get", "CurrentRegion"), step("get", "Address")]),
        "excel.range.set-formula": command("excel", "handle", [step("set", "Formula", value_argument="formula")]),
        "excel.range.get-formula": command("excel", "handle", [step("get", "Formula")]),
        "excel.application.calculate": command("excel", "application", [step("call", "Calculate")]),
        "excel.range.set-number-format": command("excel", "handle", [step("set", "NumberFormat", value_argument="format")]),
        "excel.range.get-number-format": command("excel", "handle", [step("get", "NumberFormat")]),
        "excel.range.font-bold": command("excel", "handle", [step("get", "Font"), step("set", "Bold", value_argument="value")]),
        "excel.range.font-get-bold": command("excel", "handle", [step("get", "Font"), step("get", "Bold")]),
        "excel.range.font-italic": command("excel", "handle", [step("get", "Font"), step("set", "Italic", value_argument="value")]),
        "excel.range.font-get-italic": command("excel", "handle", [step("get", "Font"), step("get", "Italic")]),
        "excel.range.font-size": command("excel", "handle", [step("get", "Font"), step("set", "Size", value_argument="value")]),
        "excel.range.font-get-size": command("excel", "handle", [step("get", "Font"), step("get", "Size")]),
        "excel.range.font-name": command("excel", "handle", [step("get", "Font"), step("set", "Name", value_argument="value")]),
        "excel.range.font-get-name": command("excel", "handle", [step("get", "Font"), step("get", "Name")]),
        "excel.range.font-color": command("excel", "handle", [step("get", "Font"), step("set", "Color", value_argument="value")]),
        "excel.range.font-get-color": command("excel", "handle", [step("get", "Font"), step("get", "Color")]),
        "excel.range.font-underline": command("excel", "handle", [step("get", "Font"), step("set", "Underline", value_argument="value")]),
        "excel.range.font-get-underline": command("excel", "handle", [step("get", "Font"), step("get", "Underline")]),
        "excel.range.font-strikethrough": command("excel", "handle", [step("get", "Font"), step("set", "Strikethrough", value_argument="value")]),
        "excel.range.font-get-strikethrough": command("excel", "handle", [step("get", "Font"), step("get", "Strikethrough")]),
        "excel.range.fill-color": command("excel", "handle", [step("get", "Interior"), step("set", "Color", value_argument="color")]),
        "excel.range.get-fill-color": command("excel", "handle", [step("get", "Interior"), step("get", "Color")]),
        "excel.range.border-style": command("excel", "handle", [step("get", "Borders"), step("set", "LineStyle", value_argument="lineStyle")]),
        "excel.range.get-border-style": command("excel", "handle", [step("get", "Borders"), step("get", "LineStyle")]),
        "excel.range.merge": command("excel", "handle", [step("set", "MergeCells", value_argument="value")], default_arguments={"value": True}),
        "excel.range.unmerge": command("excel", "handle", [step("set", "MergeCells", value_argument="value")], default_arguments={"value": False}),
        "excel.range.get-merge-cells": command("excel", "handle", [step("get", "MergeCells")]),
        "excel.range.wrap-text": command("excel", "handle", [step("set", "WrapText", value_argument="value")]),
        "excel.range.get-wrap-text": command("excel", "handle", [step("get", "WrapText")]),
        "excel.range.horizontal-alignment": command("excel", "handle", [step("set", "HorizontalAlignment", value_argument="value")]),
        "excel.range.get-horizontal-alignment": command("excel", "handle", [step("get", "HorizontalAlignment")]),
        "excel.range.vertical-alignment": command("excel", "handle", [step("set", "VerticalAlignment", value_argument="value")]),
        "excel.range.get-vertical-alignment": command("excel", "handle", [step("get", "VerticalAlignment")]),
        "excel.range.orientation": command("excel", "handle", [step("set", "Orientation", value_argument="value")]),
        "excel.range.get-orientation": command("excel", "handle", [step("get", "Orientation")]),
        "excel.range.indent-level": command("excel", "handle", [step("set", "IndentLevel", value_argument="value")]),
        "excel.range.get-indent-level": command("excel", "handle", [step("get", "IndentLevel")]),
        "excel.range.shrink-to-fit": command("excel", "handle", [step("set", "ShrinkToFit", value_argument="value")]),
        "excel.range.get-shrink-to-fit": command("excel", "handle", [step("get", "ShrinkToFit")]),
        "excel.range.locked": command("excel", "handle", [step("set", "Locked", value_argument="value")]),
        "excel.range.get-locked": command("excel", "handle", [step("get", "Locked")]),
        "excel.range.formula-hidden": command("excel", "handle", [step("set", "FormulaHidden", value_argument="value")]),
        "excel.range.get-formula-hidden": command("excel", "handle", [step("get", "FormulaHidden")]),
        "excel.range.column-width": command("excel", "handle", [step("get", "EntireColumn"), step("set", "ColumnWidth", value_argument="value")]),
        "excel.range.get-column-width": command("excel", "handle", [step("get", "EntireColumn"), step("get", "ColumnWidth")]),
        "excel.range.auto-fit-columns": command("excel", "handle", [step("get", "EntireColumn"), step("call", "AutoFit")]),
        "excel.range.row-height": command("excel", "handle", [step("get", "EntireRow"), step("set", "RowHeight", value_argument="value")]),
        "excel.range.get-row-height": command("excel", "handle", [step("get", "EntireRow"), step("get", "RowHeight")]),
        "excel.range.auto-fit-rows": command("excel", "handle", [step("get", "EntireRow"), step("call", "AutoFit")]),
        "excel.range.insert-row": command("excel", "handle", [step("get", "EntireRow"), step("call", "Insert")]),
        "excel.range.delete-row": command("excel", "handle", [step("get", "EntireRow"), step("call", "Delete")]),
        "excel.range.auto-filter": command("excel", "handle", [step("call", "AutoFilter", args=[arg("field", "int"), arg("criteria1", "string"), literal(None, "missing"), literal(None, "missing"), literal(True)])]),
        "excel.range.clear-contents": command("excel", "handle", [step("set", "Value2", value_argument="value")], default_arguments={"value": None}),
        "excel.range.clear-formats": command("excel", "handle", [step("call", "ClearFormats")]),
        "excel.workbook.save-as": command("excel", "handle", [step("call", "SaveAs", args=[arg("path", "path")])]),
        "excel.workbook.close": command("excel", "handle", [step("call", "Close", args=[literal(False)])]),
        "excel.application.quit": command("excel", "handle", [step("call", "Quit")]),
    }


def build_kompas_commands(view_types: dict[str, int]) -> dict[str, Any]:
    api5_defaults = {"progIds": ["KOMPAS.Application.5"]}
    api7_defaults = {"progIds": ["KOMPAS.Application.7"]}
    return {
        "kompas.api5.application": command("kompas", "application", [], default_arguments=api5_defaults),
        "kompas.api5.document2d": command("kompas", "application", [step("call", "Document2D")], default_arguments=api5_defaults),
        "kompas.api5.active-document2d": command("kompas", "application", [step("call", "ActiveDocument2D")], default_arguments=api5_defaults),
        "kompas.api5.document2d.open": command("kompas", "handle", [step("call", "ksOpenDocument", args=[arg("path", "path"), arg("visible", "bool")])]),
        "kompas.api5.line-segment": command("kompas", "handle", [step("call", "ksLineSeg", args=[arg("x1", "double"), arg("y1", "double"), arg("x2", "double"), arg("y2", "double"), arg("style", "int")])]),
        "kompas.api5.circle": command("kompas", "handle", [step("call", "ksCircle", args=[arg("xc", "double"), arg("yc", "double"), arg("radius", "double"), arg("style", "int")])]),
        "kompas.api5.arc-by-3-points": command("kompas", "handle", [step("call", "ksArcBy3Points", args=[arg("x1", "double"), arg("y1", "double"), arg("x2", "double"), arg("y2", "double"), arg("x3", "double"), arg("y3", "double"), arg("style", "int")])]),
        "kompas.api5.point": command("kompas", "handle", [step("call", "ksPoint", args=[arg("x", "double"), arg("y", "double"), arg("style", "int")])]),
        "kompas.api5.text": command("kompas", "handle", [step("call", "ksText", args=[arg("x", "double"), arg("y", "double"), arg("angle", "double"), arg("height", "double"), arg("aspect", "double"), arg("bitVector", "int"), arg("text", "string")])]),
        "kompas.api5.point-arrow": command("kompas", "handle", [step("call", "ksPointArraw", args=[arg("x", "double"), arg("y", "double"), arg("angle", "double"), arg("term", "int")])]),
        "kompas.api5.get-param-struct": command("kompas", "application", [step("call", "GetParamStruct", args=[arg("structType", "int")])], default_arguments=api5_defaults),
        "kompas.api5.rect-param.configure": command("kompas", "handle", [step("set", "x", value_argument="x"), step("set", "y", value_argument="y"), step("set", "ang", value_argument="ang"), step("set", "height", value_argument="height"), step("set", "width", value_argument="width"), step("set", "style", value_argument="style")]),
        "kompas.api5.rectangle": command("kompas", "handle", [step("call", "ksRectangle", args=[arg("paramHandle", "handle"), arg("centre", "short")])]),
        "kompas.api5.polygon-param.configure": command("kompas", "handle", [step("set", "count", value_argument="count"), step("set", "xc", value_argument="xc"), step("set", "yc", value_argument="yc"), step("set", "ang", value_argument="ang"), step("set", "radius", value_argument="radius"), step("set", "describe", value_argument="describe"), step("set", "style", value_argument="style")]),
        "kompas.api5.regular-polygon": command("kompas", "handle", [step("call", "ksRegularPolygon", args=[arg("paramHandle", "handle"), arg("centre", "short")])]),
        "kompas.api5.brand-leader-param.configure": command("kompas", "handle", [step("set", "dirX", value_argument="dirX"), step("set", "x", value_argument="x"), step("set", "y", value_argument="y"), step("set", "arrowType", value_argument="arrowType"), step("set", "style1", value_argument="style1"), step("set", "style2", value_argument="style2"), step("set", "cText0", value_argument="cText0"), step("set", "cText1", value_argument="cText1"), step("set", "cText2", value_argument="cText2")]),
        "kompas.api5.brand-leader": command("kompas", "handle", [step("call", "ksBrandLeader", args=[arg("paramHandle", "handle")])]),
        "kompas.api5.hatch-param.configure": command("kompas", "handle", [step("set", "x", value_argument="x"), step("set", "y", value_argument="y"), step("set", "step", value_argument="step"), step("set", "ang", value_argument="ang"), step("set", "width", value_argument="width"), step("set", "style", value_argument="style"), step("set", "color", value_argument="color")]),
        "kompas.api5.hatch": command("kompas", "handle", [step("call", "ksHatchByParam", args=[arg("paramHandle", "handle")])]),
        "kompas.api5.line-style": command("kompas", "handle", [step("call", "ksSetObjectStyle", args=[arg("objRef", "int"), arg("style", "int")])]),
        "kompas.api5.change-layer": command("kompas", "handle", [step("call", "ksChangeObjectLayer", args=[arg("objRef", "int"), arg("layerNumber", "int")])]),
        "kompas.api5.move": command("kompas", "handle", [step("call", "ksMoveObj", args=[arg("objRef", "int"), arg("x", "double"), arg("y", "double")])]),
        "kompas.api5.copy": command("kompas", "handle", [step("call", "ksCopyObj", args=[arg("objRef", "int"), arg("xOld", "double"), arg("yOld", "double"), arg("xNew", "double"), arg("yNew", "double"), arg("scale", "double"), arg("angle", "double")])]),
        "kompas.api5.rotate": command("kompas", "handle", [step("call", "ksRotateObj", args=[arg("objRef", "int"), arg("x", "double"), arg("y", "double"), arg("angle", "double")])]),
        "kompas.api5.delete": command("kompas", "handle", [step("call", "ksDeleteObj", args=[arg("objRef", "int")])]),
        "kompas.api5.exist-obj": command("kompas", "handle", [step("call", "ksExistObj", args=[arg("objRef", "int")])]),
        "kompas.api5.find-obj": command("kompas", "handle", [step("call", "ksFindObj", args=[arg("x", "double"), arg("y", "double"), arg("limit", "double")])]),
        "kompas.api5.get-object-style": command("kompas", "handle", [step("call", "ksGetObjectStyle", args=[arg("objRef", "int")])]),
        "kompas.api5.light-obj": command("kompas", "handle", [step("call", "ksLightObj", args=[arg("objRef", "int"), arg("light", "short")])]),
        "kompas.api5.keep-reference": command("kompas", "handle", [step("call", "ksKeepReference", args=[arg("objRef", "int")])]),
        "kompas.api5.get-text-length-from-reference": command("kompas", "handle", [step("call", "ksGetTextLengthFromReference", args=[arg("textRef", "int")])]),
        "kompas.api5.set-text-align": command("kompas", "handle", [step("call", "ksSetTextAlign", args=[arg("textRef", "int"), arg("align", "int")])]),
        "kompas.api5.get-text-align": command("kompas", "handle", [step("call", "ksGetTextAlign", args=[arg("textRef", "int")])]),
        "kompas.api5.line": command("kompas", "handle", [step("call", "ksLine", args=[arg("x", "double"), arg("y", "double"), arg("angle", "double")])]),
        "kompas.api5.arc-by-angle": command("kompas", "handle", [step("call", "ksArcByAngle", args=[arg("xc", "double"), arg("yc", "double"), arg("rad", "double"), arg("f1", "double"), arg("f2", "double"), arg("direction", "short"), arg("style", "int")])]),
        "kompas.api5.arc-by-point": command("kompas", "handle", [step("call", "ksArcByPoint", args=[arg("xc", "double"), arg("yc", "double"), arg("rad", "double"), arg("x1", "double"), arg("y1", "double"), arg("x2", "double"), arg("y2", "double"), arg("direction", "short"), arg("style", "int")])]),
        "kompas.api5.ann-line-segment": command("kompas", "handle", [step("call", "ksAnnLineSeg", args=[arg("x1", "double"), arg("y1", "double"), arg("x2", "double"), arg("y2", "double"), arg("term1", "short"), arg("term2", "short"), arg("style", "int")])]),
        "kompas.api5.layer-select": command("kompas", "handle", [step("call", "ksLayer", args=[arg("number", "int")])]),
        "kompas.api5.get-layer-reference": command("kompas", "handle", [step("call", "ksGetLayerReference", args=[arg("number", "int")])]),
        "kompas.api5.get-layer-number": command("kompas", "handle", [step("call", "ksGetLayerNumber", args=[arg("layerRef", "int")])]),
        "kompas.api5.zoom-all": command("kompas", "handle", [step("call", "ksZoomPrevNextOrAll", args=[arg("mode", "short")])], default_arguments={"mode": 5}),
        "kompas.api5.zoom-rect": command("kompas", "handle", [step("call", "ksZoom", args=[arg("x1", "double"), arg("y1", "double"), arg("x2", "double"), arg("y2", "double")])]),
        "kompas.api5.zoom-scale": command("kompas", "handle", [step("call", "ksZoomScale", args=[arg("x", "double"), arg("y", "double"), arg("scale", "double")])]),
        "kompas.api5.rebuild-document": command("kompas", "handle", [step("call", "ksRebuildDocument")]),
        "kompas.api5.application.refresh-window": command("kompas", "application", [step("call", "ksRefreshActiveWindow")], default_arguments=api5_defaults),
        "kompas.api5.save-dxf": command("kompas", "handle", [step("call", "ksSaveToDXF", args=[arg("path", "path")])]),
        "kompas.api5.save-document": command("kompas", "handle", [step("call", "ksSaveDocument", args=[arg("path", "path")])]),
        "kompas.api5.save-document-ex": command("kompas", "handle", [step("call", "ksSaveDocumentEx", args=[arg("path", "path"), arg("saveMode", "int")])]),
        "kompas.api5.close-document": command("kompas", "handle", [step("call", "ksCloseDocument")]),
        "kompas.api7.application": command("kompas", "application", [], default_arguments=api7_defaults),
        "kompas.api7.application.converter": command(
            "kompas",
            "application",
            [step("call", "Converter", args=[arg("library", "path")])],
            default_arguments=api7_defaults),
        "kompas.api7.converter.get-filter": command(
            "kompas",
            "handle",
            [
                step(
                    "call",
                    "GetFilter",
                    args=[
                        arg("docType", "int"),
                        arg("saveAs", "bool"),
                        {
                            "Literal": 0,
                            "Converter": "int",
                            "ByRef": True,
                            "CaptureAs": "command",
                        },
                    ],
                    store_as="filter",
                ),
            ],
            return_path="stored:command"),
        "kompas.api7.converter.convert": command(
            "kompas",
            "handle",
            [
                step(
                    "call",
                    "Convert",
                    args=[
                        arg("inputFile", "path"),
                        arg("outputFile", "path"),
                        arg("command", "int"),
                        arg("showParam", "bool"),
                    ]),
            ]),
        "kompas.api7.application.open-document": command(
            "kompas",
            "application",
            [step("call", "OpenDocument", args=[arg("path", "path")])],
            default_arguments=api7_defaults),
        "kompas.api7.active-document": command("kompas", "application", [step("get", "ActiveDocument")], default_arguments=api7_defaults),
        "kompas.dsl.activeView": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
            ],
            default_arguments=api7_defaults),
        "kompas.dsl.activeView.castSymbols": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("cast", "ISymbols2DContainer"),
            ],
            default_arguments=api7_defaults),
        "kompas.dsl.activeView.querySymbols": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("queryInterface", "symbols2d"),
            ],
            default_arguments=api7_defaults),
        "kompas.dsl.activeView.tryCastTable": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("tryCast", "ITable"),
            ],
            default_arguments=api7_defaults),
        "kompas.dsl.hotReloadCast": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("cast", "symbols2d"),
            ],
            default_arguments=api7_defaults),
        "kompas.dsl.handle.castSymbols": command("kompas", "handle", [step("cast", "ISymbols2DContainer")]),
        "kompas.dsl.handle.castTable": command("kompas", "handle", [step("cast", "ITable")]),
        "kompas.api7.doc.views": command("kompas", "handle", [step("get", "ViewsAndLayersManager"), step("get", "Views")]),
        "kompas.api7.views.add": command("kompas", "handle", [step("call", "Add", args=[arg("viewType", "int")])], default_arguments={"viewType": view_types.get("vt_Normal", 1)}),
        "kompas.api7.document.view-add": command(
            "kompas",
            "handle",
            [
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("call", "Add", args=[arg("viewType", "int")]),
                step("get", "ActiveView"),
            ],
            default_arguments={"viewType": view_types.get("vt_Normal", 1)},
        ),
        "kompas.api7.view.set-name": command("kompas", "handle", [step("set", "Name", value_argument="name")]),
        "kompas.api7.view.get-name": command("kompas", "handle", [step("get", "Name")]),
        "kompas.api7.view.set-scale": command("kompas", "handle", [step("set", "Scale", value_argument="scale")]),
        "kompas.api7.view.get-scale": command("kompas", "handle", [step("get", "Scale")]),
        "kompas.api7.view.set-angle": command("kompas", "handle", [step("set", "Angle", value_argument="angle")]),
        "kompas.api7.view.get-angle": command("kompas", "handle", [step("get", "Angle")]),
        "kompas.api7.view.update": command("kompas", "handle", [step("call", "Update")]),
        "kompas.api7.view.layers": command("kompas", "handle", [step("get", "Layers")]),
        "kompas.api7.layers.add": command("kompas", "handle", [step("call", "Add")]),
        "kompas.api7.view.layer-add": command(
            "kompas",
            "handle",
            [
                step("get", "Layers"),
                step("call", "Add"),
            ],
        ),
        "kompas.api7.layer.set-name": command("kompas", "handle", [step("set", "Name", value_argument="name")]),
        "kompas.api7.layer.get-name": command("kompas", "handle", [step("get", "Name")]),
        "kompas.api7.layer.set-color": command("kompas", "handle", [step("set", "Color", value_argument="color")]),
        "kompas.api7.layer.get-color": command("kompas", "handle", [step("get", "Color")]),
        "kompas.api7.layer.set-visible": command("kompas", "handle", [step("set", "Visible", value_argument="value")]),
        "kompas.api7.layer.get-visible": command("kompas", "handle", [step("get", "Visible")]),
        "kompas.api7.layer.set-current": command("kompas", "handle", [step("set", "Current", value_argument="value")]),
        "kompas.api7.layer.get-current": command("kompas", "handle", [step("get", "Current")]),
        "kompas.api7.document.get-name": command("kompas", "handle", [step("get", "Name")]),
        "kompas.api7.document.get-path": command("kompas", "handle", [step("get", "PathName")]),
        "kompas.api7.document.save-as": command("kompas", "handle", [step("call", "SaveAs", args=[arg("path", "path")])]),
        "kompas.api7.document.close": command("kompas", "handle", [step("call", "Close")]),
        "kompas.table.writeCell": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("cast", "ISymbols2DContainer"),
                step("get", "DrawingTables"),
                step("call", "Add", args=[
                    arg("rows", "int"),
                    arg("cols", "int"),
                    arg("rowHeight", "double"),
                    arg("colWidth", "double"),
                    arg("titlePos", "int"),
                ]),
                step("cast", "ITable"),
                step("index", "Cell", args=[arg("row", "int"), arg("col", "int")]),
                step("get", "Text"),
                step("cast", "IText"),
                step("set", "Str", value_argument="value"),
            ],
            default_arguments={
                **api7_defaults,
                "rowHeight": 10.0,
                "colWidth": 40.0,
                "titlePos": 2,
            }),
        "kompas.document.saveActive": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("call", "SaveAs", args=[arg("path", "path")]),
            ],
            default_arguments=api7_defaults),
        "kompas.table.load": command(
            "kompas",
            "application",
            [
                step("get", "ActiveDocument"),
                step("get", "ViewsAndLayersManager"),
                step("get", "Views"),
                step("get", "ActiveView"),
                step("cast", "ISymbols2DContainer"),
                step("get", "DrawingTables"),
                step("call", "Load", args=[arg("path", "path")]),
            ],
            default_arguments=api7_defaults),
    }


def build_profile_commands() -> dict[str, Any]:
    view_types = enum_values(
        r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client\Interop.Kompas6Constants.dll",
        "LtViewType")
    commands: dict[str, Any] = {}
    commands.update(build_system_commands())
    commands.update(build_excel_commands())
    commands.update(build_kompas_commands(view_types))
    return commands


def make_temp_config(
    temp_root: Path,
    template_config: dict[str, Any],
    commands: dict[str, Any],
    browser_host: str,
    browser_port: int,
    kompas_api7_interop: Path | None) -> tuple[Path, dict[str, Any]]:
    config = json.loads(json.dumps(template_config))
    config.setdefault("Versions", {})
    config.setdefault("Runtime", {})
    config.setdefault("Ui", {})
    config.setdefault("Lifecycle", {})
    config.setdefault("Logging", {})
    config.setdefault("Storage", {})
    config.setdefault("Catalog", {})
    config.setdefault("Adapters", {})
    config.setdefault("Security", {})
    config["Versions"]["ConfigVersion"] = f"e2e-{utc_now()}"
    config["Runtime"]["EnvironmentName"] = "E2E"
    config["Ui"]["Url"] = f"http://{browser_host}:{browser_port}/probe.html"
    config["Ui"]["OpenMode"] = "Auto"
    config["Lifecycle"]["ShutdownPolicy"] = "WhenIdle"
    config["Lifecycle"]["IdleSeconds"] = 18
    config["Ui"]["SessionWaitSeconds"] = 4
    config["Logging"]["DebugMode"] = True
    config["Logging"]["Level"] = "Debug"
    config["Logging"]["FilePath"] = str((temp_root / "logs" / "utility.log").resolve())
    config["Storage"]["CacheDirectory"] = str((temp_root / "cache").resolve())
    config["Storage"]["ProfileDirectory"] = str((temp_root / "profiles").resolve())
    config["Storage"]["DiagnosticsDirectory"] = str((temp_root / "diagnostics").resolve())
    config["Security"]["PairingToken"] = "kwb-e2e-token"
    config["Security"]["AllowedOrigins"] = [
        f"http://{browser_host}:{browser_port}",
        f"http://localhost:{browser_port}",
    ]
    patch_kompas_adapter_config(config, kompas_api7_interop)
    config["Catalog"]["Profiles"] = [
        {
            "ProfileId": "e2e",
            "ConfigSchemaVersion": 1,
            "Description": "External browser-driven black-box E2E profile.",
            "Checksum": f"e2e-{utc_now()}",
            "Commands": {
                command_id: {
                    "CommandId": command_id,
                    **definition,
                }
                for command_id, definition in commands.items()
            },
        }
    ]
    config_path = temp_root / "config.e2e.json"
    write_json(config_path, config)
    return config_path, config


def build_scenario(
    temp_root: Path,
    config: dict[str, Any],
    browser_host: str,
    browser_port: int,
    chaos_duration: int,
    soak_seconds: int,
    latency_iterations: int,
    kompas_api7_interop: Path | None,
    kompas_api7_interop_candidates: list[str],
    kompas_table_template: Path | None,
    kompas_table_template_candidates: list[str]) -> dict[str, Any]:
    workspace = temp_root / "workspace"
    workspace.mkdir(parents=True, exist_ok=True)
    excel_dir = workspace / "excel"
    kompas_dir = workspace / "kompas"
    system_dir = workspace / "system"
    for path in (excel_dir, kompas_dir, system_dir):
        path.mkdir(parents=True, exist_ok=True)

    kompas_sample = find_kompas_sample()
    kompas_sample_copy = kompas_dir / (kompas_sample.name if kompas_sample else "sample.frw")
    kompas_legacy_copy = kompas_dir / (
        f"{kompas_sample.stem}-legacy{kompas_sample.suffix}" if kompas_sample else "sample-legacy.frw")
    kompas_save_copy = kompas_dir / "sample-save-copy.frw"
    kompas_api5_save_copy = kompas_dir / "sample-api5-save-copy.frw"
    kompas_dsl_save_copy = kompas_dir / "sample-dsl-save-copy.frw"
    kompas_export_dxf = kompas_dir / "sample-export.dxf"
    kompas_table_template_copy = kompas_dir / "sample-table-template.tbl"
    kompas_export_library = Path(r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\ImpExp\dwgdxfExp.rtw")
    if kompas_sample:
        shutil.copy2(kompas_sample, kompas_sample_copy)
        shutil.copy2(kompas_sample, kompas_legacy_copy)
    if kompas_table_template:
        shutil.copy2(kompas_table_template, kompas_table_template_copy)

    scenario = {
        "utilityBaseUrl": config["Server"]["ListenUrl"],
        "pairingToken": config["Security"]["PairingToken"],
        "profileId": "e2e",
        "hostBaseUrl": f"http://{browser_host}:{browser_port}",
        "configTemplate": config,
        "durations": {
            "connectionSoakSeconds": soak_seconds,
            "chaosSeconds": chaos_duration,
            "latencyIterations": latency_iterations,
            "reconnectIntervalSeconds": 60,
            "postDisconnectGraceSeconds": 5,
            "idleWindowSeconds": int(config["Lifecycle"]["IdleSeconds"]),
        },
        "system": {
            "root": str(system_dir.resolve()),
            "alphaFile": str((system_dir / "alpha.txt").resolve()),
            "betaFile": str((system_dir / "beta.txt").resolve()),
            "movedFile": str((system_dir / "moved.txt").resolve()),
            "backupFile": str((system_dir / "backup.txt").resolve()),
            "bytesFile": str((system_dir / "bytes.bin").resolve()),
            "zipFile": str((system_dir / "bundle.zip").resolve()),
            "extractDir": str((system_dir / "zip-out").resolve()),
            "nestedDir": str((system_dir / "nested").resolve()),
            "emptyDir": str((system_dir / "empty").resolve()),
            "registry": {
                "hive": "CurrentUser",
                "keyPath": f"Software\\WebBridge.Utility.E2E\\{utc_now()}",
                "valueName": "RunnerValue",
                "valueKind": "String"
            },
            "httpPingUrl": f"http://{browser_host}:{browser_port}/test/ping",
            "httpJsonUrl": f"http://{browser_host}:{browser_port}/test/json"
        },
        "excel": {
            "workbookPath": str((excel_dir / "runner.xlsx").resolve()),
            "sheetName": "RunnerSheet",
            "matrix": [[1, 2], [3, 4]],
            "formula": "=SUM(B2:C3)",
            "numberFormat": "0.00",
            "fillColor": 65535,
            "alignmentCenter": -4108,
            "borderLineStyle": 1,
            "visualPauseMs": 5000
        },
        "kompas": {
            "hasSample": kompas_sample is not None,
            "samplePath": str(kompas_sample.resolve()) if kompas_sample else None,
            "sampleCopyPath": str(kompas_sample_copy.resolve()),
            "legacyCopyPath": str(kompas_legacy_copy.resolve()) if kompas_sample else None,
            "sampleDirectory": str(kompas_dir.resolve()),
            "saveCopyPath": str(kompas_save_copy.resolve()),
            "api5SaveCopyPath": str(kompas_api5_save_copy.resolve()),
            "dslSaveCopyPath": str(kompas_dsl_save_copy.resolve()),
            "exportDxfPath": str(kompas_export_dxf.resolve()),
            "exportLibraryPath": str(kompas_export_library.resolve()) if kompas_export_library.exists() else None,
            "interopAssemblyPath": str(kompas_api7_interop.resolve()) if kompas_api7_interop else None,
            "interopAssemblyCandidates": kompas_api7_interop_candidates,
            "tableTemplatePath": str(kompas_table_template_copy.resolve()) if kompas_table_template else None,
            "tableTemplateCandidates": kompas_table_template_candidates,
            "structTypes": enum_values(
                r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client\Interop.Kompas6Constants.dll",
                "StructType2DEnum"),
            "viewTypes": enum_values(
                r"C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client\Interop.Kompas6Constants.dll",
                "LtViewType")
        },
        "catalogs": {
            "system": read_json(CATALOGS_DIR / "system.catalog.json"),
            "excel": read_json(CATALOGS_DIR / "excel.catalog.json"),
            "kompas": read_json(CATALOGS_DIR / "kompas.catalog.json"),
            "chaos": { **read_json(CATALOGS_DIR / "chaos.catalog.json"), "durationSeconds": chaos_duration },
        },
    }
    return scenario


def start_host(temp_root: Path, scenario_path: Path, config_path: Path) -> subprocess.Popen[str]:
    host_log = temp_root / "logs" / "host-stdio.log"
    host_log.parent.mkdir(parents=True, exist_ok=True)
    stream = host_log.open("w", encoding="utf-8")
    process = subprocess.Popen(
        [
            sys.executable,
            str(TOOLS_DIR / "e2e_host.py"),
            "--static-root",
            str(RUNNER_DIR),
            "--scenario",
            str(scenario_path),
            "--artifacts-dir",
            str((temp_root / "artifacts").resolve()),
            "--logs-dir",
            str((temp_root / "logs").resolve()),
            "--utility-exe",
            str(PUBLISHED_EXE.resolve()),
            "--config-path",
            str(config_path.resolve()),
        ],
        cwd=str(REPO_ROOT),
        stdout=stream,
        stderr=subprocess.STDOUT,
        text=True)
    process._stdio_stream = stream  # type: ignore[attr-defined]
    return process


def start_utility(config_path: Path, temp_root: Path) -> subprocess.Popen[str]:
    log_path = temp_root / "logs" / "utility-launcher-stdio.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    stream = log_path.open("w", encoding="utf-8")
    process = subprocess.Popen(
        [str(PUBLISHED_EXE.resolve()), "--config", str(config_path.resolve())],
        cwd=str(PUBLISHED_EXE.parent.resolve()),
        stdout=stream,
        stderr=subprocess.STDOUT,
        text=True)
    process._stdio_stream = stream  # type: ignore[attr-defined]
    return process


def stop_process(process: subprocess.Popen[str], *, kill: bool = False) -> None:
    if process.poll() is None:
        if kill:
            process.kill()
        else:
            process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
    stream = getattr(process, "_stdio_stream", None)
    if stream is not None:
        stream.close()


def browser_driver(temp_root: Path, scenario: dict[str, Any], browser: str) -> subprocess.CompletedProcess[str]:
    node_executable = "node.exe" if os.name == "nt" else "node"
    return run(
        [
            node_executable,
            str(TOOLS_DIR / "browser_driver.mjs"),
            "--url",
            f"{scenario['hostBaseUrl']}/index.html",
            "--status-url",
            f"{scenario['hostBaseUrl']}/api/status",
            "--artifacts-dir",
            str((temp_root / "artifacts").resolve()),
            "--logs-dir",
            str((temp_root / "logs").resolve()),
            "--browser",
            browser,
            "--timeout-ms",
            str(3600 * 1000),
            "--slowmo-ms",
            "75",
        ],
        cwd=NODE_RUNTIME_DIR,
        timeout=3900)


def collect_summary(temp_root: Path, browser_result: subprocess.CompletedProcess[str]) -> dict[str, Any]:
    artifacts = temp_root / "artifacts"
    main_report = artifacts / "report-main.json"
    chaos_report = artifacts / "report-chaos.json"
    summary: dict[str, Any] = {
        "success": False,
        "baselineCompleted": main_report.exists(),
        "chaosCompleted": chaos_report.exists(),
        "browserDriver": {
            "returncode": browser_result.returncode,
            "stdout": browser_result.stdout,
            "stderr": browser_result.stderr,
        },
        "tempRoot": str(temp_root.resolve()),
        "artifacts": {
            "mainReport": str(main_report.resolve()),
            "chaosReport": str(chaos_report.resolve()),
            "utilityLog": str((temp_root / "logs" / "utility.log").resolve()),
            "browserConsoleLog": str((temp_root / "logs" / "browser-console.jsonl").resolve()),
            "traceLog": str((temp_root / "logs" / "http-ws-trace.jsonl").resolve()),
            "hostLog": str((temp_root / "logs" / "host.log").resolve()),
        },
        "failedChecks": [],
    }

    if main_report.exists():
        main_payload = read_json(main_report)
        summary["main"] = {
            "success": bool(main_payload.get("success")),
            "failedChecks": main_payload.get("failedChecks", []),
        }
        summary["failedChecks"].extend(main_payload.get("failedChecks", []))
    if chaos_report.exists():
        chaos_payload = read_json(chaos_report)
        summary["chaos"] = {
            "success": bool(chaos_payload.get("success")),
            "failedChecks": chaos_payload.get("failedChecks", []),
        }
        summary["failedChecks"].extend(chaos_payload.get("failedChecks", []))

    audit_issues = analyze_runtime_artifacts(temp_root, main_report)
    summary["artifactAudit"] = audit_issues
    if audit_issues:
        summary["failedChecks"].extend(
            {
                "id": issue["id"],
                "title": issue["id"],
                "success": False,
                "expected": issue["expected"],
                "actual": issue["actual"],
                "classification": issue["classification"],
                "severity": issue["severity"],
                "details": issue.get("details", ""),
            }
            for issue in audit_issues
        )

    summary["success"] = browser_result.returncode == 0 and not summary["failedChecks"]
    return summary


def analyze_runtime_artifacts(temp_root: Path, main_report_path: Path) -> list[dict[str, Any]]:
    issues: list[dict[str, Any]] = []

    browser_console = read_json_lines(temp_root / "logs" / "browser-console.jsonl")
    for entry in browser_console:
        level = str(entry.get("level") or entry.get("type") or "").lower()
        text = str(entry.get("text") or "")
        if not text:
            continue

        if "chaos-" in text and "-failed" in text:
            continue

        if "heartbeat-" in text and "-failed" in text and "WebSocket is not open" in text:
            continue

        if "-failed " in text and "expected-rejection" not in text:
            issues.append({
                "id": "artifact.browser-console.unexpected-failed-log",
                "expected": "В browser console не должно быть неожиданных failed-сообщений.",
                "actual": text,
                "classification": "harness defect",
                "severity": "high",
            })
            break

        if level in {"error", "pageerror", "driver-error"}:
            if "Failed to load resource" in text and any(code in text for code in ("400", "401", "403", "409")):
                continue
            if "ERR_CONNECTION_REFUSED" in text or "ERR_NETWORK_IO_SUSPENDED" in text:
                continue
            issues.append({
                "id": "artifact.browser-console.unexpected-error",
                "expected": "В browser console не должно быть неожиданных ошибок.",
                "actual": text,
                "classification": "harness defect",
                "severity": "high",
            })
            break

    driver_console = read_json_lines(temp_root / "logs" / "driver-console.jsonl")
    for entry in driver_console:
        level = str(entry.get("type") or "").lower()
        text = str(entry.get("text") or "")
        if level in {"error", "pageerror", "driver-error"}:
            if "Failed to load resource" in text and any(code in text for code in ("400", "401", "403", "409")):
                continue
            if "ERR_CONNECTION_REFUSED" in text or "ERR_NETWORK_IO_SUSPENDED" in text:
                continue
            issues.append({
                "id": "artifact.driver-console.unexpected-error",
                "expected": "В driver console не должно быть неожиданных ошибок.",
                "actual": text,
                "classification": "harness defect",
                "severity": "high",
            })
            break

    utility_log = temp_root / "logs" / "utility.log"
    if utility_log.exists():
        for line in utility_log.read_text(encoding="utf-8", errors="replace").splitlines():
            if "[Error]" in line or "[Critical]" in line:
                issues.append({
                    "id": "artifact.utility-log.error-level-entry",
                    "expected": "В utility.log не должно быть Error/Critical записей.",
                    "actual": line.strip(),
                    "classification": "product defect",
                    "severity": "high",
                })
                break

    if main_report_path.exists():
        main_payload = read_json(main_report_path)
        for check in main_payload.get("checks", []):
            if check.get("success") and "Error:" in str(check.get("details") or ""):
                issues.append({
                    "id": "artifact.report.success-with-error-details",
                    "expected": "У успешных checks не должно быть error-like details.",
                    "actual": f'{check.get("id")}: {check.get("details")}',
                    "classification": "harness defect",
                    "severity": "high",
                })
                break

    return issues


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--soak-seconds", type=int, default=600)
    parser.add_argument("--chaos-seconds", type=int, default=480)
    parser.add_argument("--latency-iterations", type=int, default=120)
    parser.add_argument("--keep-temp", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    ensure_publish()
    ensure_node_runtime()
    browser = pick_browser()
    template_config = read_json(CONFIG_TEMPLATE)
    kompas_api7_interop, kompas_api7_interop_candidates = find_kompas_api7_interop(template_config)
    kompas_table_template, kompas_table_template_candidates = find_kompas_table_template()

    temp_root = Path(tempfile.gettempdir()) / f"kwb-e2e-run-{utc_now()}"
    if temp_root.exists():
        shutil.rmtree(temp_root)
    (temp_root / "logs").mkdir(parents=True, exist_ok=True)
    (temp_root / "artifacts").mkdir(parents=True, exist_ok=True)

    commands = build_profile_commands()
    config_path, config = make_temp_config(
        temp_root,
        template_config,
        commands,
        "127.0.0.1",
        5510,
        kompas_api7_interop)
    scenario = build_scenario(
        temp_root,
        config,
        "127.0.0.1",
        5510,
        args.chaos_seconds,
        args.soak_seconds,
        args.latency_iterations,
        kompas_api7_interop,
        kompas_api7_interop_candidates,
        kompas_table_template,
        kompas_table_template_candidates)
    scenario_path = temp_root / "scenario.json"
    write_json(scenario_path, scenario)

    host_process = start_host(temp_root, scenario_path, config_path)
    try:
        wait_http_json("http://127.0.0.1:5510/api/status", timeout_seconds=30)
        utility_process = start_utility(config_path, temp_root)
        try:
            wait_health(config["Server"]["ListenUrl"], timeout_seconds=90)
            probe_observed = False
            for _ in range(25):
                status = wait_http_json("http://127.0.0.1:5510/api/status", timeout_seconds=5)
                if status.get("probeHits", 0) > 0:
                    probe_observed = True
                    break
                time.sleep(1)

            driver_result = browser_driver(temp_root, scenario, browser)
            summary = collect_summary(temp_root, driver_result)
            summary["autoOpenProbeObserved"] = probe_observed
            write_json(temp_root / "artifacts" / "analysis-summary.json", summary)
            print(json.dumps(summary, ensure_ascii=False, indent=2))
            print(f"Artifacts preserved at: {temp_root}")
            return 0 if summary["success"] else 1
        finally:
            stop_process(utility_process, kill=False)
    finally:
        stop_process(host_process, kill=False)


if __name__ == "__main__":
    raise SystemExit(main())
