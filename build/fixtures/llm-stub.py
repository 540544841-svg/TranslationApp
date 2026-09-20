"""Minimal OpenAI-compatible stub used only by build/verify-batch5-runtime.ps1.

Records every incoming system/user prompt as one JSON line so the harness can assert
that FR-050 (context) and FR-051 (style) actually reach the wire, then replies with a
fixed translation. Usage: python llm-stub.py <port> <log-path>
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

port = int(sys.argv[1])
log_path = sys.argv[2]
counter = 0


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self):
        global counter
        length = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(length).decode("utf-8"))
        messages = {m.get("role"): m.get("content", "") for m in payload.get("messages", [])}
        counter += 1
        with open(log_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps({
                "n": counter,
                "path": self.path,
                "system": messages.get("system"),
                "user": messages.get("user"),
            }, ensure_ascii=False) + "\n")
        # Batch-6 dictionary requests are recognisable by their system prompt ("你是词典…");
        # answer with the JSON contract (trailing comma on purpose: the parser must repair it)
        # so FR-056 can be exercised end to end.
        if "词典" in (messages.get("system") or ""):
            reply = '{"wordhead":"apple","phonetic":"/ˈæpl/","senses":["n. 苹果","n. 苹果状的东西"],}'
        else:
            # three sentences on purpose: batch 5b checks that shadow reading splits the
            # translation into per-sentence lines and highlights them one by one
            reply = f"桩译文{counter}。这是第二句内容。最后是第三句！"
        body = json.dumps({
            "id": f"stub-{counter}",
            "choices": [{"index": 0, "message": {"role": "assistant", "content": reply}}],
            "usage": {"total_tokens": 1},
        }).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


HTTPServer(("127.0.0.1", port), Handler).serve_forever()
