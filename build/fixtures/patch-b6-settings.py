"""Batch 6b runtime settings: per-app language pairs + the first-run onboarding card.

Resets the onboarding flag so the card comes up through the real startup path, and
pre-seeds one rule for Notepad so "the quick window opens already set to Russian"
tests the matching code rather than an empty list.

Env in: B6_SETTINGS (path). Everything else in the user's settings.json is preserved;
verify-batch6b-runtime.ps1 restores the file afterwards.
"""
import json
import os

path = os.environ["B6_SETTINGS"]

with open(path, encoding="utf-8-sig") as handle:
    data = json.load(handle)

data.update({
    "OnboardingShown": False,
    "AppLanguageMemoryEnabled": True,
    "AppLanguagePairs": [
        {"Process": "charmap", "SourceLanguage": "auto", "TargetLanguage": "ru"},
    ],
    # global default stays Chinese: only the *session* may switch to Russian
    "TargetLanguage": "zh-CN",
    "PrivacyMode": False,
})

with open(path, "w", encoding="utf-8") as handle:
    json.dump(data, handle, ensure_ascii=False, indent=2)

print("patched", path)
