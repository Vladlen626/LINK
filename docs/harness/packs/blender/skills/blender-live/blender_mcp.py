"""CLI client of the Blender MCP bridge (the Blender Lab "MCP" extension, socket 127.0.0.1:9876).

Run: python blender_mcp.py <command> [arguments]   (or: uv run --no-project blender_mcp.py ...)

  ping                  - Blender's version, the open file, the scene
  py <file.py|-> [args] - run code in the open Blender (bpy is there; the answer is the dict `result`,
                          print() goes to stdout). ARGS is the list of strings after the file name.

Environment: BLENDER_MCP_HOST (127.0.0.1), BLENDER_MCP_PORT (9876), BLENDER_MCP_TIMEOUT (seconds, 300).
Protocol: JSON {"type": "execute", "code": ..., "strict_json": false} + '\\0' -> JSON + '\\0'.
"""
import json
import os
import socket
import sys

# Not localhost: Windows tries IPv6 ::1 first, and the extension listens on IPv4 only - each call lost ~2 s.
HOST = os.environ.get("BLENDER_MCP_HOST", "127.0.0.1")
PORT = int(os.environ.get("BLENDER_MCP_PORT", "9876"))
TIMEOUT = float(os.environ.get("BLENDER_MCP_TIMEOUT", "300"))


def execute(code):
    with socket.create_connection((HOST, PORT), timeout=TIMEOUT) as s:
        s.sendall((json.dumps({"type": "execute", "code": code, "strict_json": False}) + "\0").encode("utf-8"))
        buf = bytearray()
        while not buf.endswith(b"\0"):
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.rstrip(b"\0").decode("utf-8"))


PING = """
import bpy
result = {"version": bpy.app.version_string, "file": bpy.data.filepath, "dirty": bpy.data.is_dirty,
          "scene": bpy.context.scene.name, "fps": bpy.context.scene.render.fps,
          "frame": bpy.context.scene.frame_current}
"""


def main(argv):
    if len(argv) < 2 or argv[1] in ("-h", "--help"):
        print(__doc__)
        return 0
    cmd = argv[1]
    if cmd == "ping":
        resp = execute(PING)
    elif cmd == "py":
        src = argv[2] if len(argv) > 2 else "-"
        code = sys.stdin.read() if src == "-" else open(src, encoding="utf-8").read()
        resp = execute("ARGS = {!r}\n".format(argv[3:]) + code)
    else:
        print("unknown command: " + cmd, file=sys.stderr)
        return 2
    for key in ("stdout", "stderr"):
        if resp.get(key):
            sys.stdout.write(resp[key])
    if resp.get("status") != "ok":
        print(resp.get("message", resp), file=sys.stderr)
        return 1
    if resp.get("result"):
        print(json.dumps(resp["result"], ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main(sys.argv))
