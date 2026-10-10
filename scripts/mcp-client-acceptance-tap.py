"""Transparent stdio tap for an MCP server. Forwards bytes unchanged and appends
each newline-delimited JSON-RPC frame to a log with its direction.
Usage: python -I mcp-client-acceptance-tap.py <log-file> <sqlharness-home> <server-command> [args...]
The log contains tool results; keep it local.
"""
import subprocess
import sys
import threading
import time
import os


if len(sys.argv) < 4:
    raise SystemExit("usage: mcp-client-acceptance-tap.py <log-file> <sqlharness-home> <server-command> [args...]")

log_path, sqlharness_home, command = sys.argv[1], sys.argv[2], sys.argv[3:]
log = open(log_path, "a", encoding="utf-8")
lock = threading.Lock()


def record(direction, line):
    with lock:
        log.write(f"{time.strftime('%H:%M:%S')} {direction} {line.decode('utf-8', 'replace').rstrip()}\n")
        log.flush()


server_environment = os.environ.copy()
server_environment["SQLHARNESS_HOME"] = sqlharness_home
server = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=server_environment)


def pump(source, sink, direction):
    for line in iter(source.readline, b""):
        record(direction, line)
        sink.write(line)
        sink.flush()
    if direction == "C->S" and server.stdin:
        server.stdin.close()


def drain_stderr():
    for line in iter(server.stderr.readline, b""):
        record("ERR", line)


threading.Thread(target=pump, args=(sys.stdin.buffer, server.stdin, "C->S"), daemon=True).start()
threading.Thread(target=drain_stderr, daemon=True).start()
try:
    pump(server.stdout, sys.stdout.buffer, "S->C")
finally:
    sys.exit(server.wait())
