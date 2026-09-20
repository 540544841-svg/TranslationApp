"""Batch-5 runtime verification: point the AI engine at the local stub so FR-050/051
can be exercised end to end. Only the keys listed here are touched; everything else in
the user's settings.json is preserved. Restored by the caller (verify-batch5-runtime.ps1).

Env in: B5_SETTINGS (path), B5_KEY (enc:... DPAPI string), B5_PORT.
"""
import json
import os

path = os.environ["B5_SETTINGS"]
port = os.environ["B5_PORT"]

with open(path, encoding="utf-8-sig") as handle:
    data = json.load(handle)

data.update({
    "Engine": "llm",
    "LlmBaseUrl": f"http://localhost:{port}/v1",
    "LlmModel": "stub-model",
    "LlmApiKeyEncrypted": os.environ["B5_KEY"],
    "LlmPrompt": "",
    "PrivacyMode": False,
    # TM off: each test sentence must really reach the engine, otherwise the stub log
    # count proves nothing about what was sent.
    "TmReuseEnabled": False,
    "LlmContextEnabled": True,
    "TranslationStyle": "none",
    "TargetLanguage": "zh-CN",
})

# Batch 5b runs the same patch with the review line and shadow reading switched on.
if os.environ.get("B5_BATCH5B") == "1":
    data.update({
        "DailyReviewEnabled": True,
        "DailyReviewDate": "",
        "DailyReviewIndex": 0,
        "ShadowReadingEnabled": True,
        "ShadowPauseMs": 600,
    })

with open(path, "w", encoding="utf-8") as handle:
    json.dump(data, handle, ensure_ascii=False, indent=2)

print("patched", path)
