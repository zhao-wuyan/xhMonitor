"""
Gemini Bridge Script for Claude Agent Skills.
Wraps the Gemini CLI to provide a JSON-based interface for Claude.
"""

import json
import os
import queue
import subprocess
import threading
import time
import shutil
import argparse
from pathlib import Path
from typing import Generator, List, Optional


def run_shell_command(cmd: List[str], cwd: Optional[str] = None) -> Generator[str, None, None]:
    """Execute a command and stream its output line-by-line."""
    popen_cmd = cmd.copy()
    gemini_path = shutil.which('gemini') or cmd[0]
    popen_cmd[0] = gemini_path

    process = subprocess.Popen(
        popen_cmd,
        shell=False,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        universal_newlines=True,
        encoding='utf-8',
        cwd=cwd,
    )

    output_queue: "queue.Queue[Optional[str]]" = queue.Queue()
    GRACEFUL_SHUTDOWN_DELAY = 0.3

    def is_turn_completed(line: str) -> bool:
        try:
            data = json.loads(line)
            return data.get("type") == "turn.completed"
        except (json.JSONDecodeError, AttributeError, TypeError):
            return False

    def read_output() -> None:
        if process.stdout:
            for line in iter(process.stdout.readline, ""):
                stripped = line.strip()
                output_queue.put(stripped)
                if is_turn_completed(stripped):
                    time.sleep(GRACEFUL_SHUTDOWN_DELAY)
                    process.terminate()
                    break
            process.stdout.close()
        output_queue.put(None)

    thread = threading.Thread(target=read_output)
    thread.start()

    while True:
        try:
            line = output_queue.get(timeout=0.5)
            if line is None:
                break
            yield line
        except queue.Empty:
            if process.poll() is not None and not thread.is_alive():
                break

    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    thread.join(timeout=5)

    while not output_queue.empty():
        try:
            line = output_queue.get_nowait()
            if line is not None:
                yield line
        except queue.Empty:
            break


def windows_escape(prompt):
    """Windows style string escaping."""
    result = prompt.replace('\\', '\\\\')
    result = result.replace('"', '\\"')
    result = result.replace('\n', '\\n')
    result = result.replace('\r', '\\r')
    result = result.replace('\t', '\\t')
    result = result.replace('\b', '\\b')
    result = result.replace('\f', '\\f')
    result = result.replace("'", "\\'")
    return result


def main():
    parser = argparse.ArgumentParser(description="Gemini Bridge")
    parser.add_argument("--PROMPT", required=True, help="Instruction for the task to send to gemini.")
    parser.add_argument("--cd", required=True, type=Path, help="Set the workspace root for gemini before executing the task.")
    parser.add_argument("--sandbox", action="store_true", default=False, help="Run in sandbox mode. Defaults to `False`.")
    parser.add_argument("--SESSION_ID", default="", help="Resume the specified session of the gemini. Defaults to empty string, start a new session.")
    parser.add_argument("--return-all-messages", action="store_true", help="Return all messages (e.g. reasoning, tool calls, etc.) from the gemini session. Set to `False` by default, only the agent's final reply message is returned.")
    parser.add_argument("--model", default="", help="The model to use for the gemini session. This parameter is strictly prohibited unless explicitly specified by the user.")

    args = parser.parse_args()

    cd: Path = args.cd
    if not cd.exists():
        result = {
            "success": False,
            "error": f"The workspace root directory `{cd.absolute().as_posix()}` does not exist. Please check the path and try again."
        }
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return

    PROMPT = args.PROMPT
    if os.name == "nt":
        PROMPT = windows_escape(PROMPT)

    cmd = ["gemini", "--prompt", PROMPT, "-o", "stream-json"]

    if args.sandbox:
        cmd.extend(["--sandbox"])

    if args.model:
        cmd.extend(["--model", args.model])

    if args.SESSION_ID:
        cmd.extend(["--resume", args.SESSION_ID])

    all_messages = []
    agent_messages = ""
    success = True
    err_message = ""
    thread_id = None

    for line in run_shell_command(cmd, cwd=cd.absolute().as_posix()):
        try:
            line_dict = json.loads(line.strip())
            all_messages.append(line_dict)
            item_type = line_dict.get("type", "")
            item_role = line_dict.get("role", "")
            if item_type == "message" and item_role == "assistant":
                if (
                    "The --prompt (-p) flag has been deprecated and will be removed in a future version. Please use a positional argument for your prompt. See gemini --help for more information.\n"
                    in line_dict.get("content", "")
                ):
                    continue
                agent_messages = agent_messages + line_dict.get("content", "")
            if line_dict.get("session_id") is not None:
                thread_id = line_dict.get("session_id")

        except json.JSONDecodeError:
            err_message += "\n\n[json decode error] " + line
            continue

        except Exception as error:
            err_message += "\n\n[unexpected error] " + f"Unexpected error: {error}. Line: {line!r}"
            break
    
    result = {}
    
    if thread_id is None:
        success = False
        err_message = "Failed to get `SESSION_ID` from the gemini session. \n\n" + err_message
    else:
        result["SESSION_ID"] = thread_id

    if success and len(agent_messages) == 0:
        success = False
        err_message = (
            "Failed to retrieve `agent_messages` data from the Gemini session. This might be due to Gemini performing a tool call. You can continue using the `SESSION_ID` to proceed with the conversation. \n\n "
            + err_message
        )
    
    
    if success:
        result["agent_messages"] = agent_messages
    else:
        result["error"] = err_message
        
    result["success"] = success

    if args.return_all_messages:
        result["all_messages"] = all_messages

    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
