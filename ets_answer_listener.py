#!/usr/bin/env pythonw
"""Watch ETS and run the bundled exam converter and answer worker."""

from __future__ import annotations

import ctypes
import argparse
import os
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path


ETS_EXES = {
    "etsshell.exe",
    "ets.exe",
    "etscore.exe",
    "shell.exe",
}
ETS_INSTALL_ROOT = Path(os.environ.get("ETS_INSTALL_ROOT", r"C:\Program Files (x86)\ETS"))
ETS_EXE = Path(os.environ.get("ETS_EXE_PATH", str(ETS_INSTALL_ROOT / "Ets.exe")))
APPDATA_ROOT = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
PACK_ROOT = Path(os.environ.get("ETS_PACK_ROOT", str(APPDATA_ROOT / "ETS")))
SCRIPT_ROOT = Path(__file__).resolve().parent
CONVERTER = SCRIPT_ROOT / "exam_pack_to_markdown_pdf.py"
AUTO_ANSWER = SCRIPT_ROOT / "ets_auto_answer.py"
LOG_PATH = PACK_ROOT / "ets_answer_listener.log"
DEBUG_PORT = int(os.environ.get("ETS_DEBUG_PORT", "10086"))
ANSWER_CONFIG = Path(os.environ.get("ETS_ANSWER_CONFIG", str(SCRIPT_ROOT / "ets_answer_config.yaml")))
_INSTANCE_MUTEX = None
ERROR_ALREADY_EXISTS = 183
TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


def log(message: str) -> None:
    try:
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {message}\n")
    except OSError:
        pass


def acquire_instance_mutex() -> bool:
    """Single-instance guard that does not depend on lock-file permissions."""
    global _INSTANCE_MUTEX
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.restype = wintypes.HANDLE
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel32.CreateMutexW(None, False, "Local\\ETSAnswerListenerEngine-v1")
    if not handle:
        # Never block startup if the API itself is unavailable.
        return True
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        kernel32.CloseHandle(handle)
        return False
    _INSTANCE_MUTEX = handle
    return True


def ets_is_running(ets_exe: Path | None = None) -> bool:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
    ]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    ets_exe = (ets_exe or ETS_EXE).resolve()
    install_root = os.path.normcase(os.path.normpath(str(ets_exe.parent)))
    expected_paths = {
        os.path.normcase(os.path.normpath(str(ets_exe))),
        os.path.normcase(os.path.normpath(str(ets_exe.parent / "ETSShell.exe"))),
        os.path.normcase(os.path.normpath(str(ets_exe.parent / "Ets.exe"))),
        os.path.normcase(os.path.normpath(str(ets_exe.parent / "shell.exe"))),
    }
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == INVALID_HANDLE_VALUE:
        return False
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.casefold() in ETS_EXES:
                handle = kernel32.OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION, False, entry.th32ProcessID
                )
                if not handle:
                    ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
                    continue
                try:
                    buffer = ctypes.create_unicode_buffer(32768)
                    length = wintypes.DWORD(len(buffer))
                    if not kernel32.QueryFullProcessImageNameW(
                        handle, 0, buffer, ctypes.byref(length)
                    ):
                        ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
                        continue
                    path = os.path.normcase(os.path.normpath(buffer.value))
                    if path in expected_paths:
                        return True
                    if entry.szExeFile.casefold() in {"etscore.exe", "shell.exe"}:
                        try:
                            if os.path.commonpath([path, install_root]) == install_root:
                                return True
                        except ValueError:
                            pass
                finally:
                    kernel32.CloseHandle(handle)
            ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
        return False
    finally:
        kernel32.CloseHandle(snapshot)


def worker_command(mode: str, *arguments: str) -> list[str]:
    prefix = [sys.executable]
    if not getattr(sys, "frozen", False):
        prefix.append(str(SCRIPT_ROOT / "ets_assistant.py"))
    return [*prefix, mode, *arguments]


def open_log(path: Path):
    """Open an append-only log, or return None when Windows blocks the path.

    A low-integrity launch can read the data folder but cannot modify its
    existing files. Answering still has to start in that case, so a log that
    cannot be opened must never stop the worker.
    """
    try:
        return path.open("a", encoding="utf-8")
    except OSError:
        return None


def start_converter_watcher() -> subprocess.Popen[bytes]:
    log_handle = open_log(PACK_ROOT / "ets_converter.log")
    process = subprocess.Popen(
        worker_command("--converter-watch", str(PACK_ROOT)),
        cwd=str(PACK_ROOT),
        stdin=subprocess.DEVNULL,
        stdout=log_handle or subprocess.DEVNULL,
        stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    if log_handle is not None:
        log_handle.close()
    return process


def start_assistant_window() -> bool:
    """Open the elevated WinUI application; never fall back to the retired interface."""
    try:
        app_root = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else SCRIPT_ROOT
        winui_exe = app_root / "_winui" / "ETS-Assistant-WinUI.exe"
        if not winui_exe.is_file():
            log(f"WinUI assistant is missing: {winui_exe}. No legacy interface will be launched.")
            return False

        # The WinUI manifest requires elevation so it can control ETS windows.
        # CreateProcess/Popen cannot satisfy that request; ShellExecute's runas
        # verb starts the app through the normal Windows UAC flow.
        shell_execute = ctypes.WinDLL("shell32", use_last_error=True).ShellExecuteW
        shell_execute.argtypes = [
            wintypes.HWND,
            wintypes.LPCWSTR,
            wintypes.LPCWSTR,
            wintypes.LPCWSTR,
            wintypes.LPCWSTR,
            ctypes.c_int,
        ]
        shell_execute.restype = ctypes.c_void_p
        result = shell_execute(None, "runas", str(winui_exe), None, str(app_root), 1)
        if result is None or result <= 32:
            log(f"Windows could not elevate {winui_exe} (ShellExecute result {result}).")
            return False
        log(f"{winui_exe.name} administrator launch requested through UAC.")
        return True
    except OSError as exc:
        log(f"Could not open the assistant window: {exc}")
        return False


def start_auto_answer_worker() -> subprocess.Popen[bytes] | None:
    if not getattr(sys, "frozen", False) and not AUTO_ANSWER.is_file():
        log(f"Auto-answer worker is missing: {AUTO_ANSWER.name}")
        return None
    try:
        log_handle = open_log(PACK_ROOT / "ets_auto_answer.log")
        process = subprocess.Popen(
            worker_command(
                "--answer-watch",
                "--root", str(PACK_ROOT),
                "--config", str(ANSWER_CONFIG),
                "--devtools", f"http://127.0.0.1:{DEBUG_PORT}/json/list",
            ),
            cwd=str(PACK_ROOT),
            stdin=subprocess.DEVNULL,
            stdout=log_handle or subprocess.DEVNULL,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        # Popen inherits this handle; closing the parent's copy does not stop logging.
        if log_handle is not None:
            log_handle.close()
        log("Auto-answer worker started; it will not click a final-submit button.")
        return process
    except OSError as exc:
        log(f"Could not start auto-answer worker: {exc}")
        return None


def stop_process(process: subprocess.Popen[bytes] | None, label: str, *, tree: bool = False) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        if tree:
            # Stop the listener-owned worker tree; this helper runs without a console window.
            subprocess.run(
                ["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
                creationflags=subprocess.CREATE_NO_WINDOW,
                check=False,
            )
        else:
            process.terminate()
        process.wait(timeout=3)
    except (OSError, subprocess.TimeoutExpired):
        try:
            process.kill()
            process.wait(timeout=2)
        except (OSError, subprocess.TimeoutExpired):
            log(f"Could not fully stop {label} (PID {process.pid})")
            return
    log(f"Stopped {label} (PID {process.pid})")


def main(argv: list[str] | None = None) -> int:
    global PACK_ROOT, LOG_PATH, ETS_EXE, ETS_INSTALL_ROOT, DEBUG_PORT, ANSWER_CONFIG
    if os.name != "nt":
        return 1
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=PACK_ROOT)
    parser.add_argument("--ets-exe", type=Path, default=ETS_EXE)
    parser.add_argument("--debug-port", type=int, default=DEBUG_PORT)
    parser.add_argument("--config", type=Path, default=ANSWER_CONFIG)
    args = parser.parse_args(argv)
    PACK_ROOT = args.root.resolve()
    LOG_PATH = PACK_ROOT / "ets_answer_listener.log"
    ETS_EXE = args.ets_exe.resolve()
    ETS_INSTALL_ROOT = ETS_EXE.parent
    DEBUG_PORT = args.debug_port
    ANSWER_CONFIG = args.config.resolve()
    PACK_ROOT.mkdir(parents=True, exist_ok=True)
    if not acquire_instance_mutex():
        log("Another ETS listener is already running; this instance will exit.")
        return 0
    log(f"Listener started ({Path(sys.executable).name}); watching process names: " + ", ".join(sorted(ETS_EXES)))
    if not getattr(sys, "frozen", False) and not CONVERTER.is_file():
        log(f"Required converter is missing: {CONVERTER}")
        return 1
    was_running = False
    terminal: subprocess.Popen[bytes] | None = None
    worker: subprocess.Popen[bytes] | None = None
    while True:
        running = ets_is_running(ETS_EXE)
        if running and not was_running:
            log("ETS process detected; starting automation workers.")
        if running and not was_running and (terminal is None or terminal.poll() is not None):
            log("ETS process detected; starting the converter watcher and answer worker.")
            try:
                terminal = start_converter_watcher()
                log("Converter watcher started; progress is in ets_converter.log.")
            except OSError as exc:
                log(f"Could not start the converter watcher: {exc}")
                terminal = None
        if running and (worker is None or worker.poll() is not None):
            worker = start_auto_answer_worker()
        if running and not was_running:
            log("ETS process detected; opening the assistant window.")
            try:
                start_assistant_window()
            except Exception as exc:
                log(f"Could not open the assistant window: {exc}")
        if not running and was_running:
            stop_process(worker, "auto-answer listener")
            stop_process(terminal, "converter watcher", tree=True)
            worker = None
            terminal = None
            log("ETS exited; answer processes stopped. Waiting for ETS to start again.")
        was_running = running
        time.sleep(0.5)


if __name__ == "__main__":
    raise SystemExit(main())
