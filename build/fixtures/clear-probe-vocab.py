"""Removes the probe vocabulary rows seeded by seed-probe-vocab.py.

Usage: python clear-probe-vocab.py <history.db>
"""
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
try:
    cursor = connection.cursor()
    cursor.execute("DELETE FROM Vocabulary WHERE SourceText LIKE 'b5probe-vocab%'")
    connection.commit()
    print("vocab probe rows removed:", cursor.rowcount)
finally:
    connection.close()
