"""Deletes the rows the batch-5 runtime probe wrote into the user's history database.

Usage: python clear-probe-rows.py <history.db>
Only rows whose SourceText starts with the harness marker are removed.
"""
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
try:
    cursor = connection.cursor()
    cursor.execute("DELETE FROM History WHERE SourceText LIKE 'b5probe%'")
    connection.commit()
    print("probe rows removed:", cursor.rowcount)
finally:
    connection.close()
