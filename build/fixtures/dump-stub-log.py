"""Prints the batch-5 stub log as ASCII-safe evidence lines for the PowerShell harness
(a GBK console would mangle the Chinese prompts and hide exactly what we are checking).

Usage: python dump-stub-log.py <log-path>
"""
import json
import sys

for line in open(sys.argv[1], encoding="utf-8"):
    record = json.loads(line)
    system = record.get("system") or ""
    print("n=%d path=%s user=%s" % (record["n"], record["path"], json.dumps(record.get("user"))))
    print("  system=%s" % system.encode("unicode_escape").decode("ascii"))
    print("  has_context=%s has_style=%s" % ("上一段原文" in system, "风格要求" in system))
