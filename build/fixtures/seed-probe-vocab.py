"""Seeds probe vocabulary rows so FR-052's daily review line has something to show.

Idempotent: only tops the count up to five probe rows. clear-probe-vocab.py removes them
again (same marker), so the user's real word list is untouched afterwards.

Usage: python seed-probe-vocab.py <history.db>
"""
import sqlite3
import sys
import time

MARKER = "b5probe-vocab"

connection = sqlite3.connect(sys.argv[1])
try:
    cursor = connection.cursor()
    cursor.execute("SELECT COUNT(*) FROM Vocabulary WHERE SourceText LIKE ?", (MARKER + "%",))
    existing = cursor.fetchone()[0]
    now_ms = int(time.time() * 1000)
    added = 0
    for i in range(existing, 5):
        cursor.execute(
            """
            INSERT INTO Vocabulary (CreatedAtMs, SourceText, TranslatedText, SourceLanguage, TargetLanguage)
            VALUES (?, ?, ?, 'en', 'zh-CN');
            """,
            (now_ms + i, f"{MARKER} word {i}", f"探针词{i}"),
        )
        added += 1
    connection.commit()
    print("vocab probe rows added:", added)
finally:
    connection.close()
