#!/usr/bin/env python3
"""Drive a speaking-question page: start recording, play the pre-generated TTS
WAV into the virtual cable, stop, and advance to the next question.

The page must be a speaking practice/exam page that uses the native iFlytek
recorder (buttons: .btn-play, .btn-stopRecord, text 下一步 / 练习结束).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import wave
from pathlib import Path

from ets_auto_answer import CDPClient, CDPError, devtools_targets

APP_ROOT = (
    Path(sys.executable).resolve().parent
    if getattr(sys, "frozen", False)
    else Path(__file__).resolve().parent
)
DATA_ROOT = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming")) / "ETS"
LOG_PATH = DATA_ROOT / "ets_speaking_auto.log"
CABLE_PLAYER = next(
    (path for path in (
        APP_ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
        APP_ROOT / ".build" / "tools" / "cableplayer" / "out" / "CablePlayer.exe",
    ) if path.is_file()),
    APP_ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
)
PLAYER_SPEED_FACTOR = 1.30


def find_session(explicit: str | None, data_root: Path) -> Path:
    if explicit:
        return Path(explicit)
    candidates = [
        data_root / "_assistant" / "speaking_session.json",
        data_root / "temp" / "speaking_session.json",
    ]
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("speaking_session.json not found")


def cleanup_temp_audio(root: Path) -> None:
    """Delete generated TTS WAVs and the virtual-mic copy after a finished run."""
    removed = 0
    total = 0
    folders = {root / "temp" / "tts-test", APP_ROOT / "temp" / "tts-test"}
    for folder in folders:
        if not folder.is_dir():
            continue
        for wav in folder.glob("*.wav"):
            try:
                total += wav.stat().st_size
                wav.unlink()
                removed += 1
            except OSError:
                pass
    speaking_input = Path(os.environ.get("APPDATA", "")) / "ETS" / "_assistant" / "speaking_input.wav"
    try:
        if speaking_input.is_file():
            total += speaking_input.stat().st_size
            speaking_input.unlink()
            removed += 1
    except OSError:
        pass
    log(f"cleanup: removed {removed} audio file(s), {total / 1024 / 1024:.1f} MB")


def log(message: str) -> None:
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} {message}\n")
    except OSError:
        pass
    if sys.stdout is not None:
        try:
            print(message, flush=True)
        except OSError:
            pass


def wav_duration_seconds(path: str | Path) -> float:
    """Return the actual PCM duration so a detached player process cannot end early."""
    try:
        with wave.open(str(path), "rb") as audio:
            sample_rate = audio.getframerate()
            if sample_rate <= 0:
                return 0.0
            return max(0.0, audio.getnframes() / sample_rate)
    except (OSError, wave.Error):
        return 0.0


def playback_duration_seconds(path: str | Path) -> float:
    """The automatic run time-stretches TTS to fit ETS's fixed recording window."""
    return wav_duration_seconds(path) / PLAYER_SPEED_FACTOR


def terminate_player(player: subprocess.Popen[str] | None) -> None:
    if player is None or player.poll() is not None:
        return
    try:
        player.terminate()
        player.wait(timeout=2.0)
    except subprocess.TimeoutExpired:
        try:
            player.kill()
            player.wait(timeout=2.0)
        except (OSError, subprocess.TimeoutExpired):
            pass
    except OSError:
        pass


def finished_player_status(player: subprocess.Popen[str]) -> tuple[bool, str] | None:
    """Check the player's terminal message; elapsed WAV time alone cannot prove output."""
    if player.poll() is None:
        return None
    try:
        output, _ = player.communicate(timeout=0.2)
    except subprocess.TimeoutExpired:
        return None
    output = output or ""
    lowered = output.casefold()
    markers = [
        marker
        for marker in ("PLAY", "DONE", "TIMEOUT", "media failed", "no VB-Audio render device", "missing:")
        if marker.casefold() in lowered
    ]
    error_lines = [
        line.strip()
        for line in output.splitlines()
        if any(marker in line.casefold() for marker in (
            "media failed", "timeout", "no vb-audio render device", "missing:",
            "waveout", "invalid wav", "unsupported wav", "playback error",
        ))
    ]
    failed = (
        player.returncode != 0
        or "DONE" not in output
        or "TIMEOUT" in output
        or "media failed" in lowered
        or "no VB-Audio render device" in lowered
        or "missing:" in lowered
    )
    status = " | ".join(error_lines[-3:]) if failed and error_lines else ",".join(markers)
    return failed, status or f"exit={player.returncode}; no DONE marker"


def page_state(client: CDPClient) -> dict:
    raw = client.evaluate(
        "(()=>{const text=document.body.innerText;return JSON.stringify({"
        "text,"
        "stop:!!document.querySelector('button.btn-stopRecord'),"
        "play:!!document.querySelector('button.btn-play'),"
        "exam:!!document.querySelector('.exam-panel, .exam-progress, video.examVideo'),"
        "recording:/(?:^|\\n)\\s*录音中(?:\\s|$)/.test(text),"
        "fix:[...document.querySelectorAll('button,span')].some(e=>e.offsetParent!==null&&e.innerText&&e.innerText.trim()==='订正'),"
        "next:[...document.querySelectorAll('button')].some(b=>b.offsetParent!==null&&b.innerText.trim()==='下一步'&&!b.disabled)"
        "})})()"
    )
    return json.loads(raw)


def click_next(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=[...document.querySelectorAll('button')].find(e=>e.offsetParent!==null&&e.innerText.trim()==='下一步'&&!e.disabled);"
        "if(!b)return false;b.click();return true;})()"
    ) is True


def click_play(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=document.querySelector('button.btn-play');if(!b)return false;b.click();return true;})()"
    ) is True


def click_stop(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=document.querySelector('button.btn-stopRecord');if(!b)return false;b.click();return true;})()"
    ) is True


def click_fix(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const el=[...document.querySelectorAll('button,span')].find(e=>e.offsetParent!==null&&e.innerText&&e.innerText.trim()==='订正'&&e.children.length===0);"
        "if(!el)return false;const b=el.closest('button');(b||el).click();return true;})()"
    ) is True


def _main(argv: list[str] | None = None) -> int:
    global DATA_ROOT, LOG_PATH
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", default=None)
    parser.add_argument("--data-root", type=Path, default=DATA_ROOT)
    parser.add_argument("--log", type=Path, default=None)
    parser.add_argument("--start-index", type=int, default=0)
    parser.add_argument("--player", default=str(CABLE_PLAYER))
    parser.add_argument("--max-question-seconds", type=float, default=90)
    parser.add_argument("--keep-temp", action="store_true", help="keep generated WAVs after the run")
    args = parser.parse_args(argv)

    DATA_ROOT = args.data_root.resolve()
    LOG_PATH = args.log.resolve() if args.log else DATA_ROOT / "ets_speaking_auto.log"
    session = json.loads(find_session(args.session, DATA_ROOT).read_text(encoding="utf-8-sig"))
    wavs = [item["WavPath"] for item in session.get("Items", [])]
    if not wavs:
        log("session has no WAV files")
        return 1
    if not Path(args.player).is_file():
        log(f"口语音频播放器不存在：{Path(args.player).name}；已在开始录音前中止。")
        return 6
    invalid_wavs = [
        (index + 1, Path(path).name)
        for index, path in enumerate(wavs)
        if not Path(path).is_file() or wav_duration_seconds(path) <= 0
    ]
    if invalid_wavs:
        details = ", ".join(f"第 {index} 题 ({name})" for index, name in invalid_wavs[:5])
        if len(invalid_wavs) > 5:
            details += f" 等 {len(invalid_wavs)} 题"
        log(f"口语音频文件已缺失或无效：{details}；已在 ETS 开始录音前中止，没有跳题。请重新生成音频。")
        return 6
    log(f"session pack={session.get('PackId')} items={len(wavs)} mode={session.get('Mode')}")

    targets = devtools_targets()
    if not targets:
        log("ETS DevTools page not found (is ETS running with port 10086?)")
        return 2
    client = CDPClient(targets[0]["webSocketDebuggerUrl"])
    page_url = client.evaluate("location.href")
    match = re.search(r"[?&#]set_id=(\d+)", page_url)
    page_pack = match.group(1) if match else ""
    mock_exam = re.search(r"#/modelTestMode(?:[/?#]|$)", page_url, re.IGNORECASE) is not None
    if page_pack and str(session.get("PackId")) != page_pack:
        log(f"session pack {session.get('PackId')} does not match page pack {page_pack}")
        client.close()
        return 3
    player: subprocess.Popen[str] | None = None
    index = max(0, args.start_index)
    started_at = 0.0
    expected_playback_end = 0.0
    playback_timeout_at = 0.0
    playback_wait_logged = False
    player_done_logged = False
    finished = False
    played = 0
    fix_attempts = 0
    mock_waiting_for_recording_end = False

    initial = page_state(client)
    log(f"page={page_url[:160]}")
    log(f"mock={mock_exam} exam={initial['exam']} play={initial['play']} fix={initial['fix']} text={initial['text'][:120]!r}")
    if not mock_exam and not initial["exam"] and not initial["fix"]:
        log("当前页面不是口语练习/答题页，未播放音频，保留 WAV。请先打开一道口语题再点开始自动口语。")
        client.close()
        return 4

    try:
        while True:
            state = page_state(client)
            text = state["text"]
            player_status = finished_player_status(player) if player is not None else None
            if player_status is not None and player_status[0]:
                return_code = player.returncode
                if not mock_exam and state["stop"]:
                    click_stop(client)
                log(
                    f"音频播放器未确认成功输出 (code={return_code}, status={player_status[1]})；"
                    "已中止自动口语，不会推进下一题。"
                )
                return 7
            if player_status is not None and not player_done_logged:
                remaining = expected_playback_end - time.monotonic()
                if remaining > 0.75:
                    if not mock_exam and state["stop"]:
                        click_stop(client)
                    log(
                        f"音频播放器在 WAV 预计时长前退出约 {remaining:.1f}s "
                        f"(code={player.returncode}, status={player_status[1]})；"
                        "已中止自动口语，不推进下一题。"
                    )
                    return 7
                log(f"CablePlayer completed successfully (status={player_status[1]})")
                player_done_logged = True
            audio_finished = (
                player is None
                or (time.monotonic() >= expected_playback_end and player.poll() is not None)
            )
            if audio_finished and ("练习结束" in text or "提交成功" in text) and (
                not mock_exam or (played > 0 and not state["recording"])
            ):
                if state["fix"] and fix_attempts < 2:
                    fix_attempts += 1
                    log("练习已结束，点击“订正”重新开始。")
                    click_fix(client)
                    time.sleep(2.0)
                    continue
                log("practice finished")
                finished = True
                break
            if mock_exam:
                # Simulation exams control the recorder themselves. They have no
                # .btn-play/.btn-stopRecord buttons, so wait for their visible
                # recording phase and let ETS advance after its timer expires.
                if state["recording"]:
                    if player is None and not mock_waiting_for_recording_end and index < len(wavs):
                        started_at = time.monotonic()
                        duration = wav_duration_seconds(wavs[index])
                        playback_duration = duration / PLAYER_SPEED_FACTOR
                        expected_playback_end = started_at + playback_duration + 0.25
                        playback_timeout_at = started_at + max(args.max_question_seconds, playback_duration + 10.0)
                        playback_wait_logged = False
                        player_done_logged = False
                        played += 1
                        log(
                            f"[{index + 1}/{len(wavs)}] mock recording, playing {Path(wavs[index]).name} "
                            f"({duration:.1f}s source, {playback_duration:.1f}s at {PLAYER_SPEED_FACTOR:.2f}x)"
                        )
                        player = subprocess.Popen(
                            [args.player, "--speed", f"{PLAYER_SPEED_FACTOR:.2f}", wavs[index]],
                            stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT,
                            text=True,
                            encoding="utf-8",
                            errors="replace",
                        )
                    elif (
                        player is not None
                        and player.poll() is not None
                        and time.monotonic() >= expected_playback_end
                    ):
                        log(f"[{index + 1}/{len(wavs)}] playback ended; waiting for ETS recording to end")
                        player = None
                        mock_waiting_for_recording_end = True
                    elif player is not None and time.monotonic() > playback_timeout_at:
                        terminate_player(player)
                        log(f"[{index + 1}/{len(wavs)}] 播放超时；已终止本轮自动口语，不推进下一题。")
                        return 8
                    time.sleep(0.2)
                    continue
                if player is not None:
                    now = time.monotonic()
                    playback_complete = now >= expected_playback_end and player.poll() is not None
                    if playback_complete:
                        log(f"[{index + 1}/{len(wavs)}] playback ended; ETS recording already ended")
                        player = None
                        mock_waiting_for_recording_end = True
                    else:
                        elapsed = max(0.0, now - started_at)
                        terminate_player(player)
                        log(
                            f"[{index + 1}/{len(wavs)}] ETS 录音窗口在音频结束前关闭 "
                            f"(已播放约 {elapsed:.1f}/{playback_duration_seconds(wavs[index]):.1f}s，"
                            f"原始音频 {wav_duration_seconds(wavs[index]):.1f}s，{PLAYER_SPEED_FACTOR:.2f}x)；"
                            "本轮已中止，未把该题标记为完成。请检查 ETS 录音时长和麦克风设置后重试。"
                        )
                        return 9
                if mock_waiting_for_recording_end:
                    index += 1
                    mock_waiting_for_recording_end = False
                    log(f"[{index}/{len(wavs)}] mock recording ended")
                if index >= len(wavs):
                    log("all WAV items consumed")
                    finished = True
                    break
                time.sleep(0.3)
                continue
            if state["stop"]:
                if player is None and index < len(wavs):
                    started_at = time.monotonic()
                    duration = wav_duration_seconds(wavs[index])
                    playback_duration = duration / PLAYER_SPEED_FACTOR
                    expected_playback_end = started_at + playback_duration + 0.25
                    playback_timeout_at = started_at + max(args.max_question_seconds, playback_duration + 10.0)
                    playback_wait_logged = False
                    player_done_logged = False
                    played += 1
                    log(
                        f"[{index + 1}/{len(wavs)}] recording, playing {Path(wavs[index]).name} "
                        f"({duration:.1f}s source, {playback_duration:.1f}s at {PLAYER_SPEED_FACTOR:.2f}x)"
                    )
                    player = subprocess.Popen(
                        [args.player, "--speed", f"{PLAYER_SPEED_FACTOR:.2f}", wavs[index]],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                    )
                if (
                    player is not None
                    and player.poll() is not None
                    and time.monotonic() >= expected_playback_end
                ):
                    time.sleep(0.8)
                    click_stop(client)
                    log(f"[{index + 1}/{len(wavs)}] stopped")
                    player = None
                    index += 1
                    time.sleep(1.5)
                elif player is not None and time.monotonic() > playback_timeout_at:
                    terminate_player(player)
                    click_stop(client)
                    log(f"[{index + 1}/{len(wavs)}] 播放超时；已中止本轮自动口语，不推进下一题。")
                    return 8
                continue

            # Not recording: if a recording was auto-stopped, close the player.
            if player is not None:
                now = time.monotonic()
                if now >= expected_playback_end and player.poll() is not None:
                    log(f"[{index + 1}/{len(wavs)}] playback ended; ETS recording already ended")
                    player = None
                    index += 1
                    time.sleep(1.0)
                    continue
                elapsed = max(0.0, now - started_at)
                terminate_player(player)
                log(
                    f"[{index + 1}/{len(wavs)}] ETS 录音窗口在音频结束前关闭 "
                    f"(已播放约 {elapsed:.1f}/{playback_duration_seconds(wavs[index]):.1f}s，"
                    f"原始音频 {wav_duration_seconds(wavs[index]):.1f}s，{PLAYER_SPEED_FACTOR:.2f}x)；"
                    "本轮已中止，未把该题标记为完成。请检查 ETS 录音时长和麦克风设置后重试。"
                )
                return 9

            if index >= len(wavs):
                log("all WAV items consumed")
                finished = True
                break
            if state["next"]:
                click_next(client)
                time.sleep(1.6)
            elif state["play"]:
                click_play(client)
                time.sleep(2.0)
            else:
                time.sleep(0.5)
    finally:
        terminate_player(player)
        client.close()
    if finished and played > 0 and not args.keep_temp:
        cleanup_temp_audio(DATA_ROOT)
    elif played == 0:
        log("本次没有实际播放音频，保留 temp WAV 以便重试。")
    return 0


def main(argv: list[str] | None = None) -> int:
    try:
        return _main(argv)
    except (OSError, CDPError, ValueError, TypeError) as exc:
        log(f"自动口语已停止：ETS 页面连接中断或状态无效 ({exc})。可以返回 ETS 后重新开始。")
        return 5


if __name__ == "__main__":
    raise SystemExit(main())
