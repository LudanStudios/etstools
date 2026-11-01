#!/usr/bin/env python3
"""Fill visible ETS multiple-choice questions from a local exam-pack JSON.

The worker talks only to the ETS page's loopback Chrome DevTools endpoint and
uses the page's own click handlers for choices. It never clicks a final-submit button;
the ETS page may still advance or submit automatically after the last question.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import random
import re
import socket
import struct
import time
import urllib.request
import uuid
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse, urlsplit

from ets_activity import clean_text, record_answer


DEFAULT_ROOT = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming")) / "ETS"
DEFAULT_DEVTOOLS = "http://127.0.0.1:10086/json/list"
DEFAULT_CONFIG = Path(__file__).resolve().parent / "ets_answer_config.yaml"
RESET_REQUEST_FILE = "reset_request.json"
RESET_ACK_FILE = "reset_ack.json"
APP_TITLE = "E听说"
NEXT_CONFIRM_SECONDS = 3.0
NEXT_RETRY_COOLDOWN_SECONDS = 10.0
WATCH_INTERVAL_SECONDS = 0.15
LAST_NEXT_CLICK_AT = 0.0
PENDING_NEXT_CLICKS: dict[tuple[str, tuple[str, ...]], float] = {}
_DIAGNOSTIC_LOG_TIMES: dict[str, float] = {}


def warn_throttled(key: str, message: str, interval_seconds: float = 20.0) -> None:
    now = time.monotonic()
    if now - _DIAGNOSTIC_LOG_TIMES.get(key, 0.0) < interval_seconds:
        return
    _DIAGNOSTIC_LOG_TIMES[key] = now
    print(f"WARN: {message}", flush=True)


NAV_STATUS_JS = r"""(()=>{
 const docs=[document,document.querySelector('iframe.examIframe')?.contentDocument].filter(Boolean);
 const green=value=>{
   const m=String(value||'').match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
   if(!m)return false;
   const r=Number(m[1]),g=Number(m[2]),b=Number(m[3]);
   return g>=90&&g>r*1.18&&g>b*0.82;
 };
 const hasDoneMarker=tile=>{
   const nodes=[tile,...tile.querySelectorAll('*')].slice(0,64);
   for(let parent=tile.parentElement,depth=0;parent&&depth<4;parent=parent.parentElement,depth++)nodes.push(parent);
   for(const node of nodes){
     const tokens=(node.getAttribute('class')||'').split(/\s+/).filter(Boolean);
     if(tokens.some(token=>/(^|[-_])(answered|complete|completed|done|finished|success|correct)([-_]|$)/i.test(token)&&! /^(un|not)[-_]/i.test(token)))return true;
     const label=[node.getAttribute('aria-label'),node.getAttribute('title'),node.getAttribute('data-status')].filter(Boolean).join(' ');
     if(/已答|已完成|已作答|\banswered\b|\bcompleted\b|\bfinished\b|\bdone\b/i.test(label))return true;
     if(/^[✓✔√]$/.test((node.textContent||'').trim()))return true;
     const view=node.ownerDocument.defaultView,style=view.getComputedStyle(node);
     if(green(style.color)||green(style.backgroundColor)||green(style.borderColor)||green(style.fill)||green(style.stroke))return true;
     for(const pseudo of ['::before','::after']){
       const ps=view.getComputedStyle(node,pseudo);
       if(ps.content&&ps.content!=='none'&&/[✓✔√]/.test(ps.content))return true;
       if(green(ps.color)||green(ps.backgroundColor)||green(ps.borderColor))return true;
     }
   }
   return false;
 };
 const progress=()=>{
   const text=docs.map(doc=>doc.body?.innerText||'').join('\n');
   const m=text.match(/(?:已答|已完成|已作答)\s*[:：]?\s*(\d+)\s*(?:\/|／)\s*(\d+)/)||text.match(/(?:answered|completed)\s*[:：]?\s*(\d+)\s*(?:\/|of)\s*(\d+)/i);
   return m?Number(m[1]):null;
 };
 return number=>{
   number=Number(number);
   if(!Number.isFinite(number)||number<1)return {found:false,done:false,answeredCount:progress()};
   const expected=String(number),regions=[];
   for(const doc of docs){
     const width=doc.documentElement?.clientWidth||doc.defaultView?.innerWidth||0;
     for(const el of doc.querySelectorAll('button,a,[role="button"],li,div,span')){
       if((el.textContent||'').replace(/\s+/g,'').trim()!==expected)continue;
       let node=el;
       for(let depth=0;node&&depth<4;depth++,node=node.parentElement){
         const r=node.getBoundingClientRect();
         if(r.width<28||r.height<22||r.width>180||r.height>120||r.left<width*0.58)continue;
         regions.push({node,area:r.width*r.height,left:r.left});
       }
     }
   }
   regions.sort((a,b)=>a.area-b.area||b.left-a.left);
   const tile=regions[0]?.node||null;
   return {found:!!tile,done:!!tile&&hasDoneMarker(tile),answeredCount:progress()};
 };
})()"""


QUESTION_JS = r"""(questionOrder={})=>{
 const navStatus=""" + NAV_STATUS_JS + r""";
 const f=document.querySelector('iframe.examIframe'),d=f?.contentDocument;
 if(!d)return JSON.stringify({ready:false});
 const qs=[...d.querySelectorAll('.question2')].filter(x=>x.getBoundingClientRect().height>0&&/^\d+_\d+$/.test(x.id));
 if(!qs.length)return JSON.stringify({ready:false});
 const fr=f.getBoundingClientRect();
 const sx=fr.width/(f.clientWidth||fr.width),sy=fr.height/(f.clientHeight||fr.height);
 const frameLeft=fr.left+f.clientLeft*sx,frameTop=fr.top+f.clientTop*sy;
 const questions=qs.map(q=>{const order=questionOrder[q.id]||null,nav=order?navStatus(order):{done:false,found:false,answeredCount:null};return {id:q.id,order,recorded:nav.done,navFound:nav.found,answeredCount:nav.answeredCount,options:[...q.querySelectorAll('.choose2')].map(e=>{
   const t=e.querySelector('.choose2_text')||e,r=t.getBoundingClientRect(),er=e.getBoundingClientRect();
   const candidates=[e.querySelector('input[type="radio"]:not(:disabled)'),e.querySelector('label,[role="radio"]'),t,e];
   const clicks=candidates.filter(Boolean).map(target=>{
     const b=target.getBoundingClientRect();
     return b.width>0&&b.height>0?{x:frameLeft+(b.left+b.width/2)*sx,y:frameTop+(b.top+b.height/2)*sy}:null;
   }).filter((point,i,all)=>point&&all.findIndex(other=>other&&Math.abs(other.x-point.x)<2&&Math.abs(other.y-point.y)<2)===i);
   const hit=(r.width>0&&r.height>0)?r:er;
   const classSelected=[...e.classList].some(c=>/(?:^|[_-])(selected|checked)$/i.test(c));
   return {id:e.id,cls:e.className,text:t.innerText,selected:classSelected||e.getAttribute('aria-checked')==='true'||e.getAttribute('aria-selected')==='true'||e.getAttribute('aria-pressed')==='true'||e.matches(':checked')||!!e.querySelector(':checked'),
     clicks:clicks.length?clicks:[{x:frameLeft+(hit.left+hit.width/2)*sx,y:frameTop+(hit.top+hit.height/2)*sy}]};
  })}});
 return JSON.stringify({ready:true,questions});
}"""
CHECK_JS = r"""(id,index,number)=>{
 const navStatus=""" + NAV_STATUS_JS + r""";
 const d=document.querySelector('iframe.examIframe')?.contentDocument;
 const q=[...(d?.querySelectorAll('.question2')||[])].find(x=>x.id===id);
 const e=q?.querySelectorAll('.choose2')[index];
 const classSelected=!!e&&[...e.classList].some(c=>/(?:^|[_-])(selected|checked)$/i.test(c));
 const selected=!!e&&(classSelected||e.getAttribute('aria-checked')==='true'||e.getAttribute('aria-selected')==='true'||e.getAttribute('aria-pressed')==='true'||e.matches(':checked')||!!e.querySelector(':checked'));
 const nav=navStatus(number);
 return JSON.stringify({id:q?.id,cls:e?.className,selected,recorded:nav.done,navFound:nav.found,answeredCount:nav.answeredCount});
}"""


class CDPError(RuntimeError):
    pass


class CDPClient:
    """Small standard-library Chrome DevTools WebSocket client."""

    def __init__(self, websocket_url: str, timeout: float = 5.0):
        parsed = urlparse(websocket_url)
        if parsed.scheme != "ws" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
            raise CDPError("Refusing a non-loopback DevTools endpoint")
        self.sock = socket.create_connection((parsed.hostname, parsed.port or 80), timeout)
        self.sock.settimeout(timeout)
        self.next_id = 0
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        path = parsed.path or "/"
        if parsed.query:
            path += "?" + parsed.query
        host = parsed.hostname if parsed.port in (None, 80) else f"{parsed.hostname}:{parsed.port}"
        request = (
            f"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\n"
            f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        ).encode("ascii")
        self.sock.sendall(request)
        response = self._read_headers()
        expected = base64.b64encode(
            hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()
        ).decode("ascii")
        headers = response.decode("latin1", errors="replace").split("\r\n")
        if not headers or " 101 " not in headers[0]:
            self.close()
            raise CDPError("DevTools WebSocket handshake was rejected")
        accept = next((line.split(":", 1)[1].strip() for line in headers[1:] if line.lower().startswith("sec-websocket-accept:")), "")
        if accept != expected:
            self.close()
            raise CDPError("DevTools WebSocket handshake validation failed")

    def _read_exact(self, count: int) -> bytes:
        chunks = bytearray()
        while len(chunks) < count:
            chunk = self.sock.recv(count - len(chunks))
            if not chunk:
                raise CDPError("DevTools WebSocket closed")
            chunks.extend(chunk)
        return bytes(chunks)

    def _read_headers(self) -> bytes:
        data = bytearray()
        while not data.endswith(b"\r\n\r\n"):
            chunk = self.sock.recv(1)
            if not chunk:
                raise CDPError("DevTools handshake closed")
            data.extend(chunk)
            if len(data) > 16384:
                raise CDPError("DevTools handshake headers are too large")
        return bytes(data)

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        first = bytes([0x80 | opcode])
        length = len(payload)
        if length < 126:
            header = first + bytes([0x80 | length])
        elif length <= 0xFFFF:
            header = first + bytes([0x80 | 126]) + struct.pack("!H", length)
        else:
            header = first + bytes([0x80 | 127]) + struct.pack("!Q", length)
        mask = os.urandom(4)
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.sock.sendall(header + mask + masked)

    def _receive_message(self) -> tuple[int, bytes]:
        payload = bytearray()
        message_opcode = 0
        while True:
            first, second = self._read_exact(2)
            fin, opcode = bool(first & 0x80), first & 0x0F
            masked = bool(second & 0x80)
            length = second & 0x7F
            if length == 126:
                length = struct.unpack("!H", self._read_exact(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self._read_exact(8))[0]
            mask = self._read_exact(4) if masked else b""
            chunk = self._read_exact(length) if length else b""
            if masked:
                chunk = bytes(byte ^ mask[index % 4] for index, byte in enumerate(chunk))
            if opcode == 0x9:  # ping
                self._send_frame(0xA, chunk)
                continue
            if opcode == 0x8:
                raise CDPError("DevTools WebSocket closed")
            if opcode in (0x1, 0x2):
                message_opcode = opcode
            payload.extend(chunk)
            if fin:
                return message_opcode, bytes(payload)

    def call(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        self.next_id += 1
        request_id = self.next_id
        message = {"id": request_id, "method": method, "params": params or {}}
        self._send_frame(0x1, json.dumps(message, separators=(",", ":")).encode("utf-8"))
        while True:
            opcode, raw = self._receive_message()
            if opcode != 0x1:
                continue
            packet = json.loads(raw.decode("utf-8"))
            if packet.get("id") != request_id:
                continue
            if "error" in packet:
                raise CDPError(f"DevTools command failed: {packet['error'].get('message', 'unknown error')}")
            return packet.get("result", {})

    def evaluate(self, expression: str) -> Any:
        result = self.call("Runtime.evaluate", {
            "expression": expression,
            "returnByValue": True,
            "awaitPromise": True,
        }).get("result", {})
        if result.get("subtype") == "error":
            raise CDPError(result.get("description", "Page evaluation failed"))
        return result.get("value")

    def click(self, x: float, y: float) -> None:
        self.call("Input.dispatchMouseEvent", {
            "type": "mouseMoved", "x": x, "y": y,
        })
        self.call("Input.dispatchMouseEvent", {
            "type": "mousePressed", "x": x, "y": y,
            "button": "left", "buttons": 1, "clickCount": 1,
        })
        self.call("Input.dispatchMouseEvent", {
            "type": "mouseReleased", "x": x, "y": y,
            "button": "left", "buttons": 0, "clickCount": 1,
        })

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


def devtools_targets(endpoint: str = DEFAULT_DEVTOOLS) -> list[dict[str, Any]]:
    with urllib.request.urlopen(endpoint, timeout=2) as response:
        targets = json.loads(response.read().decode("utf-8"))
    return [
        item for item in targets
        if item.get("type") == "page" and item.get("webSocketDebuggerUrl")
        and APP_TITLE in item.get("title", "")
    ]


def pack_id_from_url(page_url: str) -> str | None:
    fragment = urlparse(page_url).fragment
    route_query = urlsplit(fragment).query
    return parse_qs(route_query).get("set_id", [None])[0]


def manual_reset_request(root: Path) -> tuple[float, str] | None:
    """Read the reset request written by the WinUI app, if one is waiting.

    The request lives outside the answer config so a reset never depends on
    restarting the worker (which would lose its in-memory progress anyway).
    """
    path = root / "_assistant" / RESET_REQUEST_FILE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict):
        return None
    requested_at = data.get("requested_at")
    if isinstance(requested_at, bool) or not isinstance(requested_at, (int, float)):
        return None
    return float(requested_at), str(data.get("pack_id") or "")


def consume_manual_reset(root: Path, requested_at: float) -> None:
    """Delete the handled request so a restarted worker cannot replay it."""
    path = root / "_assistant" / RESET_REQUEST_FILE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if float(data.get("requested_at", -1)) == requested_at:
            path.unlink(missing_ok=True)
    except (OSError, ValueError, TypeError, AttributeError):
        pass


def acknowledge_manual_reset(root: Path, requested_at: float, pack_id: str, status: str) -> None:
    """Tell the WinUI app that the reset was applied (or why it was not)."""
    path = root / "_assistant" / RESET_ACK_FILE
    payload = {
        "schema_version": 1,
        "requested_at": int(round(requested_at)),
        "applied_at": int(time.time()),
        "pack_id": pack_id,
        "status": status,
    }
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
        os.replace(temporary, path)
    except OSError:
        pass


def load_answers(pack_dir: Path) -> dict[str, str]:
    answers: dict[str, str] = {}
    for content_file in sorted(pack_dir.glob("content_*/content.json")):
        with content_file.open("r", encoding="utf-8-sig") as stream:
            data = json.load(stream)
        if data.get("structure_type") != "collector.choose":
            continue
        info = data.get("info") or {}
        content_id = str(info.get("stid") or content_file.parent.name.removeprefix("content_"))
        for index, question in enumerate(info.get("xtlist") or [], start=1):
            answer = str(question.get("answer") or "").strip().upper()
            if answer in {"A", "B", "C", "D"}:
                question_number = str(question.get("xt_xh") or index)
                answers[f"{content_id}_{question_number}"] = answer
    return answers


def load_question_order(pack_dir: Path) -> dict[str, int]:
    """Read the displayed question numbers; content IDs are not ordered IDs."""
    order: dict[str, int] = {}
    for content_file in sorted(pack_dir.glob("content_*/content2.json")):
        try:
            with content_file.open("r", encoding="utf-8-sig") as stream:
                data = json.load(stream)
        except (OSError, json.JSONDecodeError):
            continue
        info = data.get("info") or {}
        content_id = str(info.get("stid") or content_file.parent.name.removeprefix("content_"))
        for index, question in enumerate(info.get("xtlist") or [], start=1):
            label = str(question.get("xt_nr") or "")
            match = re.search(r"\d+", label)
            if match:
                question_number = str(question.get("xt_xh") or index)
                order[f"{content_id}_{question_number}"] = int(match.group())
    return order


def question_order_key(question_id: str, order: dict[str, int]) -> tuple[int, tuple[int, int]]:
    return order.get(question_id, 2**63 - 1), question_sort_key(question_id)


def first_unanswered_in_visible_range(
    remaining: list[str],
    questions: list[dict[str, Any]],
    question_order: dict[str, int],
) -> str | None:
    """Return an unanswered question that lies at or before the visible group."""
    visible_orders = [
        question_order[question_id]
        for question in questions
        if (question_id := str(question.get("id", ""))) in question_order
    ]
    if not visible_orders:
        return None
    last_visible_number = max(visible_orders)
    visible_content_ids = {
        question_id.rsplit("_", 1)[0]
        for question in questions
        if (question_id := str(question.get("id", "")))
    }
    missed = [
        question_id for question_id in remaining
        if question_id.rsplit("_", 1)[0] in visible_content_ids
        or (
            question_id in question_order
            and question_order[question_id] <= last_visible_number
        )
    ]
    return min(missed, key=lambda question_id: question_order_key(question_id, question_order)) if missed else None


def load_answer_config(path: Path = DEFAULT_CONFIG) -> tuple[float, str, float, bool]:
    """Read the small, flat YAML subset used for QA answer settings."""
    if not path.is_file():
        return 1.0, "default", 0.0, False
    values: dict[str, str] = {}
    try:
        for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue
            key, separator, value = line.partition(":")
            if not separator:
                raise ValueError(f"Expected key: value in {path.name}: {raw_line}")
            value = value.split("#", 1)[0].strip().strip("\"'")
            values[key.strip()] = value
    except OSError as exc:
        raise ValueError(f"Could not read {path.name}: {exc}") from exc

    # Ignore legacy public-service flags in old config files; no remote fallback exists.
    unknown = set(values) - {
        "accuracy_rate", "random_seed", "skip_interval_seconds", "verbose_logging_enabled",
        "free_listening_worker_url", "auto_listening_enabled", "experience_mode_enabled"
    }
    if unknown:
        raise ValueError(f"Unknown setting(s) in {path.name}: {', '.join(sorted(unknown))}")
    try:
        accuracy = float(values.get("accuracy_rate", "1.0").removesuffix("%"))
    except ValueError as exc:
        raise ValueError("accuracy_rate must be a number from 0.0 to 1.0") from exc
    if not 0.0 <= accuracy <= 1.0:
        raise ValueError("accuracy_rate must be from 0.0 to 1.0 (for example, 0.8 means 80%)")
    seed = values.get("random_seed", "default") or "default"
    try:
        skip_interval = float(values.get("skip_interval_seconds", "0.0"))
    except ValueError as exc:
        raise ValueError("skip_interval_seconds must be a number from 0.0 to 60.0") from exc
    if not 0.0 <= skip_interval <= 60.0:
        raise ValueError("skip_interval_seconds must be from 0.0 to 60.0")
    verbose_logging = values.get("verbose_logging_enabled", "false").lower() == "true"
    return accuracy, seed, skip_interval, verbose_logging


def answer_for_accuracy(
    question_id: str,
    correct_answer: str,
    option_count: int,
    accuracy: float,
    seed: str,
    session_key: str,
) -> str:
    """Choose a repeatable QA response; incorrect responses use another visible option."""
    available = "ABCD"[:min(option_count, 4)]
    if correct_answer not in available or accuracy >= 1.0 or len(available) < 2:
        return correct_answer
    digest = hashlib.sha256(
        f"{seed}|{session_key}|{question_id}".encode("utf-8")
    ).digest()
    rng = random.Random(int.from_bytes(digest[:8], "big"))
    if rng.random() < accuracy:
        return correct_answer
    wrong_answers = [letter for letter in available if letter != correct_answer]
    return wrong_answers[rng.randrange(len(wrong_answers))]


def visible_questions(
    client: CDPClient,
    question_order: dict[str, int] | None = None,
) -> list[dict[str, Any]]:
    order_json = json.dumps(question_order or {}, separators=(",", ":"))
    raw = client.evaluate(f"({QUESTION_JS})({order_json})")
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except (TypeError, json.JSONDecodeError):
        return []
    return data.get("questions", []) if data.get("ready") else []


def choose_answer(client: CDPClient, question: dict[str, Any], letter: str) -> bool:
    question_id = str(question.get("id", ""))
    index = "ABCD".find(letter)
    options = question.get("options") or []
    if index < 0 or index >= len(options):
        return False
    order_number = question.get("order")
    initial_answered_count = question.get("answeredCount")
    check_expr = f"({CHECK_JS})({json.dumps(question_id)},{index},{json.dumps(order_number)})"

    def read_state() -> dict[str, Any]:
        try:
            return json.loads(client.evaluate(check_expr) or "{}")
        except (CDPError, TypeError, json.JSONDecodeError):
            return {}

    def is_recorded() -> bool:
        return bool(read_state().get("recorded"))

    def is_confirmed() -> bool:
        state = read_state()
        if state.get("id") != question_id or not state.get("selected"):
            return False
        if state.get("recorded"):
            return True
        current_count = state.get("answeredCount")
        return (
            initial_answered_count is not None
            and current_count is not None
            and int(current_count) > int(initial_answered_count)
        )

    if question.get("recorded") or is_recorded():
        return True

    # Use a real DevTools input click first. Some ETS builds ignore HTMLElement.click()
    # because the option handler checks for a trusted user input event.
    points = options[index].get("clicks") or [options[index].get("click") or {}]
    for point in points:
        try:
            x, y = float(point["x"]), float(point["y"])
            if x < 0 or y < 0:
                continue
            client.click(x, y)
        except (CDPError, TypeError, ValueError, KeyError):
            continue
        deadline = time.monotonic() + 0.8
        while time.monotonic() < deadline:
            if is_confirmed():
                return True
            time.sleep(0.1)

    # Retain the DOM click as a compatibility fallback for ETS layouts where the
    # visible text rectangle is not the clickable hit target.
    expression = (
        "(()=>{const d=document.querySelector('iframe.examIframe')?.contentDocument;"
        f"const q=[...(d?.querySelectorAll('.question2')||[])].find(x=>x.id==={json.dumps(question_id)});"
        f"const e=q?.querySelectorAll('.choose2')[{index}];if(!e)return '';"
        "const target=e.querySelector('input[type=radio]:not(:disabled),label,[role=radio],.choose2_text')||e;"
        "target.click();return e.className;})()"
    )
    try:
        if not client.evaluate(expression):
            return False
    except CDPError:
        return False

    deadline = time.monotonic() + 1.0
    while time.monotonic() < deadline:
        if is_confirmed():
            return True
        time.sleep(0.1)
    return False


def click_next(
    client: CDPClient,
    before_ids: set[str] | None = None,
    skip_interval_seconds: float = 0.0,
) -> bool:
    """Send a trusted mouse click and confirm that ETS actually changed questions."""
    global LAST_NEXT_CLICK_AT
    if before_ids is None:
        before_ids = {str(item.get("id", "")) for item in visible_questions(client)}
    old_url = str(client.evaluate("location.href") or "")
    before_ids = {question_id for question_id in before_ids if question_id}
    transition_key = (old_url, tuple(sorted(before_ids)))

    def page_advanced() -> bool:
        current = visible_questions(client)
        current_ids = {str(item.get("id", "")) for item in current if item.get("id")}
        if current_ids and current_ids != before_ids:
            return True
        if not current_ids:
            state = client.evaluate(
                "JSON.stringify({url:location.href,text:document.querySelector('#app')?.innerText||''})"
            )
            try:
                page = json.loads(state or "{}")
            except json.JSONDecodeError:
                page = {}
            if page.get("url") != old_url or re.search(
                r"提交成功|已完成|答题结果|成绩|本次练习", page.get("text", "")
            ):
                return True
        return False

    # A previous click may still be animating or waiting on ETS. Observe that
    # transition before allowing another click on the same question group.
    pending_since = PENDING_NEXT_CLICKS.get(transition_key)
    if pending_since is not None:
        retry_at = pending_since + NEXT_RETRY_COOLDOWN_SECONDS
        while time.monotonic() < retry_at:
            if page_advanced():
                PENDING_NEXT_CLICKS.pop(transition_key, None)
                return True
            time.sleep(0.1)
        PENDING_NEXT_CLICKS.pop(transition_key, None)
        return False

    remaining_delay = skip_interval_seconds - (time.monotonic() - LAST_NEXT_CLICK_AT)
    if remaining_delay > 0:
        time.sleep(remaining_delay)
        if page_advanced():
            return True

    expr = r"(()=>{const b=[...document.querySelectorAll('button')].find(x=>x.querySelector('.icon-nextQuestion'));if(!b||b.disabled||b.getAttribute('aria-disabled')==='true'||b.getClientRects().length===0)return '';const r=b.getBoundingClientRect();return JSON.stringify({x:r.left+r.width/2,y:r.top+r.height/2})})()"
    raw = client.evaluate(expr)
    if not raw:
        return False
    try:
        point = json.loads(raw)
        old_url = str(client.evaluate("location.href") or "")
        transition_key = (old_url, tuple(sorted(before_ids)))
        LAST_NEXT_CLICK_AT = time.monotonic()
        PENDING_NEXT_CLICKS[transition_key] = LAST_NEXT_CLICK_AT
        client.click(float(point["x"]), float(point["y"]))
    except (TypeError, ValueError, KeyError, json.JSONDecodeError):
        return False
    deadline = time.monotonic() + NEXT_CONFIRM_SECONDS
    while time.monotonic() < deadline:
        if page_advanced():
            PENDING_NEXT_CLICKS.pop(transition_key, None)
            return True
        time.sleep(0.1)
    return False


def question_sort_key(question_id: str) -> tuple[int, int]:
    parts = question_id.split("_", 1)
    try:
        return int(parts[0]), int(parts[1]) if len(parts) > 1 else 0
    except ValueError:
        return (2**63 - 1, 2**63 - 1)


def run_once(
    root: Path,
    endpoint: str = DEFAULT_DEVTOOLS,
    wait_seconds: float = 12.0,
    processed: set[str] | None = None,
    accuracy: float = 1.0,
    random_seed: str = "default",
    session_key: str = "",
    skip_interval_seconds: float = 0.0,
    analysis_session_id: str | None = None,
    expected_pack_id: str | None = None,
) -> tuple[str, str | None]:
    analysis_session_id = analysis_session_id or uuid.uuid4().hex
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        try:
            targets = devtools_targets(endpoint)
        except (OSError, ValueError, json.JSONDecodeError):
            warn_throttled("devtools-error", "无法连接 ETS 调试页面，自动答题正在重试。")
            time.sleep(0.5)
            continue
        if not targets:
            warn_throttled("no-devtools-page", "未发现 ETS 答题页面，自动答题正在等待。")
        for target in targets:
            pack_id = pack_id_from_url(str(target.get("url", "")))
            if expected_pack_id is not None and pack_id != expected_pack_id:
                continue
            pack_dir = root / pack_id if pack_id and pack_id.isdigit() else None
            if not pack_dir:
                continue
            if not pack_dir.is_dir():
                warn_throttled(f"no-pack:{pack_id}", f"本机找不到试卷 {pack_id} 的答案包，自动答题正在等待。")
                continue
            try:
                answers = load_answers(pack_dir)
                if not answers:
                    warn_throttled(
                        f"no-answers:{pack_id}",
                        f"试卷 {pack_id} 没有可用的本地答案，自动答题正在等待。",
                    )
                    continue
                question_order = load_question_order(pack_dir)
                client = CDPClient(target["webSocketDebuggerUrl"])
            except (OSError, ValueError, json.JSONDecodeError, CDPError):
                continue
            try:
                questions = visible_questions(client, question_order)
                if not questions:
                    warn_throttled(
                        f"no-question-elements:{pack_id}",
                        f"试卷 {pack_id} 当前页面未找到可识别的选择题元素。",
                    )
                    continue
                visible_ids = {str(question.get("id", "")) for question in questions}
                if processed is not None and visible_ids:
                    # Only count a visible question as processed when its selected
                    # state is confirmed. Do not infer that earlier IDs were answered
                    # from ordering: ETS can temporarily omit question elements while
                    # a page is rendering, which otherwise permanently skips them.
                    for question in questions:
                        question_id = str(question.get("id", ""))
                        if question.get("recorded"):
                            processed.add(question_id)
                        else:
                            processed.discard(question_id)
                pending = [
                    question for question in questions
                    if processed is None or str(question.get("id", "")) not in processed
                ]
                if not pending:
                    remaining = [key for key in answers if key not in (processed or set())]
                    missed_id = first_unanswered_in_visible_range(remaining, questions, question_order)
                    if missed_id:
                        warn_throttled(
                            f"unanswered-before-next:{pack_id}:{missed_id}",
                            f"试卷 {pack_id} 检测到尚未确认作答的题目 {missed_id}；已暂停切题以避免漏答。",
                        )
                        return "waiting", missed_id
                    if remaining and click_next(client, visible_ids, skip_interval_seconds):
                        return "advanced", None
                    if not remaining:
                        last_id = max(answers, key=lambda question_id: question_order_key(question_id, question_order))
                        if click_next(client, visible_ids, skip_interval_seconds):
                            print("Advanced past the last local question group; ETS may complete the section automatically.", flush=True)
                            return "complete", last_id
                        print("Next button click was not confirmed; will retry while this ETS page remains open.", flush=True)
                    continue
                selected_ids: set[str] = set()
                for question in pending:
                    question_id = str(question.get("id", ""))
                    answer = answers.get(question_id)
                    if not answer:
                        print(f"No local answer for visible question {question_id}", flush=True)
                        return "waiting", question_id
                    correct_answer = answer
                    answer = answer_for_accuracy(
                        question_id, answer, len(question.get("options") or []),
                        accuracy, random_seed, session_key,
                    )
                    if not choose_answer(client, question, answer):
                        print(
                            f"WARN: 未确认 ETS 已录入答案：试卷 {pack_id}，题目 {question_id}，"
                            f"选项 {answer}，当前识别到 {len(question.get('options') or [])} 个选项。",
                            flush=True,
                        )
                        return "error", question_id
                    selected_ids.add(question_id)
                    record_answer(root, pack_dir, analysis_session_id, question_id, answer, correct_answer)
                    print(f"Selected {answer} for question {question_id}", flush=True)
                if processed is not None:
                    processed.update(selected_ids)
                remaining = [key for key in answers if key not in (processed or set()) | selected_ids]
                missed_id = first_unanswered_in_visible_range(remaining, questions, question_order)
                if missed_id:
                    warn_throttled(
                        f"unanswered-before-next:{pack_id}:{missed_id}",
                        f"试卷 {pack_id} 检测到尚未确认作答的题目 {missed_id}；已暂停切题以避免漏答。",
                    )
                    return "waiting", missed_id
                if remaining:
                    if not click_next(client, visible_ids, skip_interval_seconds):
                        print("Next button did not advance the visible questions; will retry.", flush=True)
                        return "waiting", None
                else:
                    if click_next(client, visible_ids, skip_interval_seconds):
                        print("Advanced past the last local question group; ETS may complete the section automatically.", flush=True)
                        return "complete", sorted(selected_ids, key=lambda question_id: question_order_key(question_id, question_order))[-1]
                    print("Last answers are selected, but ETS did not advance; will retry the next button.", flush=True)
                    return "waiting", None
                return "selected", sorted(selected_ids, key=lambda question_id: question_order_key(question_id, question_order))[-1]
            except (OSError, CDPError, ValueError, json.JSONDecodeError) as exc:
                print(f"Auto-answer paused safely: {exc}", flush=True)
                return "error", None
            finally:
                client.close()
        time.sleep(WATCH_INTERVAL_SECONDS)
    return "waiting", None


def watch(root: Path, endpoint: str, config_path: Path | None = None) -> int:
    handled: set[tuple[str, str]] = set()
    processed: dict[tuple[str, str], set[str]] = {}
    analysis_sessions: dict[tuple[str, str], str] = {}
    last_reset_at = 0.0
    settings_path = config_path or (root / DEFAULT_CONFIG.name)
    try:
        accuracy, random_seed, skip_interval, verbose_logging_enabled = load_answer_config(settings_path)
    except ValueError as exc:
        print(f"Invalid answer config: {exc}", flush=True)
        return 1
    print(
        f"Auto-answer worker watching ETS on loopback DevTools at port "
        f"{urlparse(endpoint).port}; configured answer accuracy: {accuracy:.0%}; "
        f"skip retry interval: {skip_interval:g}s; verbose logs: {verbose_logging_enabled}.",
        flush=True,
    )
    try:
        config_mtime = settings_path.stat().st_mtime_ns
    except OSError:
        config_mtime = None
    last_config_check = time.monotonic()
    while True:
        try:
            if time.monotonic() - last_config_check >= 1:
                last_config_check = time.monotonic()
                try:
                    current_mtime = settings_path.stat().st_mtime_ns
                    if current_mtime != config_mtime:
                        new_accuracy, new_seed, new_skip, new_verbose = load_answer_config(settings_path)
                        accuracy, random_seed, skip_interval = new_accuracy, new_seed, new_skip
                        verbose_logging_enabled = new_verbose
                        config_mtime = current_mtime
                        print("Answer settings reloaded.", flush=True)
                except (OSError, ValueError) as exc:
                    print(f"Could not reload answer settings: {exc}", flush=True)
            reset_request = manual_reset_request(root)
            if reset_request is not None and reset_request[0] <= last_reset_at:
                reset_request = None
            for target in devtools_targets(endpoint):
                pack_id = pack_id_from_url(str(target.get("url", "")))
                if not pack_id:
                    continue
                # A fresh homework_id indicates a fresh practice session.
                fragment = urlsplit(urlparse(str(target.get("url", ""))).fragment)
                params = parse_qs(fragment.query)
                session_key = (pack_id, params.get("homework_id", [""])[0])
                if reset_request is not None and (not reset_request[1] or reset_request[1] == pack_id):
                    # The user switched ETS back to question 1 and asked for a
                    # fresh pass: forget every cached decision for this pack and
                    # let the normal loop answer the visible questions again.
                    handled.discard(session_key)
                    processed.pop(session_key, None)
                    analysis_sessions.pop(session_key, None)
                    last_reset_at = reset_request[0]
                    consume_manual_reset(root, reset_request[0])
                    acknowledge_manual_reset(root, reset_request[0], pack_id, "applied")
                    print(
                        f"Manual reset applied for pack {pack_id}; "
                        "answering again from the visible question.",
                        flush=True,
                    )
                    reset_request = None
                pack_dir = root / pack_id
                if session_key in handled:
                    # ETS can reuse the same homework_id when the user starts
                    # another practice attempt. Treat a return to the first
                    # question as a fresh pass through the same pack.
                    try:
                        answers = load_answers(pack_dir)
                        question_order = load_question_order(pack_dir)
                        client = CDPClient(target["webSocketDebuggerUrl"])
                        try:
                            current = visible_questions(client, question_order)
                        finally:
                            client.close()
                        # A checked-looking radio is not proof that ETS recorded it;
                        # only the right-side completed marker confirms this attempt.
                        has_recorded_answer = any(
                            bool(question.get("recorded")) for question in current
                        )
                        if current and not has_recorded_answer:
                            handled.discard(session_key)
                            processed.pop(session_key, None)
                            analysis_sessions.pop(session_key, None)
                    except (OSError, ValueError, json.JSONDecodeError, CDPError):
                        pass
                    if session_key in handled:
                        continue
                answered = processed.setdefault(session_key, set())
                analysis_session_id = analysis_sessions.setdefault(session_key, uuid.uuid4().hex)
                status, content_id = run_once(
                    root, endpoint, wait_seconds=0.5, processed=answered,
                    accuracy=accuracy, random_seed=random_seed,
                    session_key="|".join(session_key),
                    skip_interval_seconds=skip_interval,
                    analysis_session_id=analysis_session_id,
                    expected_pack_id=pack_id,
                )
                if status == "selected" and content_id:
                    answered.add(content_id)
                elif status == "complete":
                    handled.add(session_key)
                    print(f"Answering is complete for pack {pack_id}; keeping the exam pack and converted files.", flush=True)
        except (OSError, ValueError, json.JSONDecodeError):
            pass
        time.sleep(WATCH_INTERVAL_SECONDS)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--config", type=Path, default=None)
    parser.add_argument("--devtools", default=DEFAULT_DEVTOOLS)
    parser.add_argument("--watch", action="store_true", help="wait for an active ETS question")
    args = parser.parse_args(argv)
    try:
        if args.watch:
            return watch(args.root.resolve(), args.devtools, args.config.resolve() if args.config else None)
        return 0 if run_once(args.root.resolve(), args.devtools) else 1
    except KeyboardInterrupt:
        print("Auto-answer worker stopped.", flush=True)
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
