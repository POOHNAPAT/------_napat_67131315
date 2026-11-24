"""Export a consistent SQLite snapshot to SQL for a fresh Supabase database.

Uses only Python's standard library. Never changes the source database.
Stop CourtBook before exporting and keep it stopped until cutover is verified.
"""
import argparse
import base64
from contextlib import closing
from pathlib import Path
import re
import sqlite3

TABLES = (
    "Users", "SportTypes", "Courts", "Promotions", "Bookings", "BookedSlots",
    "BlockedSlots", "TopUps", "MembershipPlans", "Memberships", "WalletEntries", "DemoPayments", "RentalEquipment", "CourtReviews", "DamageReports", "PaymentReceipts", "Expenses", "BookingRefunds", "MembershipQuotaUses", "DamageEvidence", "DamageClaims", "DamageClaimEvents", "DamageReceipts", "EquipmentStock", "EquipmentLoans", "EquipmentMovements", "BookingEntryPasses", "BookingCheckIns", "BookingCheckOuts",
)


def literal(value):
    if value is None:
        return "NULL"
    if isinstance(value, bytes):
        return "decode('" + base64.b64encode(value).decode("ascii") + "','base64')"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        # Decode UTF-8 bytes in Postgres so quotes, newlines and backslashes are exact.
        encoded = base64.b64encode(value.encode("utf-8")).decode("ascii")
        return "convert_from(decode('" + encoded + "','base64'),'UTF8')"
    raise ValueError(f"Unsupported SQLite value: {type(value).__name__}")


def export(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    if not source.is_file():
        raise ValueError("Source SQLite database does not exist")
    if source == output or output.exists():
        raise ValueError("Choose a new output file; existing files are never overwritten")
    with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True)) as original, closing(sqlite3.connect(":memory:")) as snapshot:
        original.backup(snapshot)
        if snapshot.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise ValueError("SQLite integrity check failed")
        if snapshot.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("SQLite foreign key check failed")
        schema = Path(__file__).with_name("supabase.sql").read_text(encoding="utf-8")
        # Migration inserts the actual source defaults, not the new-project seed rows.
        schema = re.sub(r"INSERT INTO MembershipPlans.*?;", "", schema, flags=re.S)
        schema = re.sub(r"INSERT INTO SportTypes.*?;", "", schema, flags=re.S)
        schema = re.sub(r"SELECT setval\(.*?;", "", schema)
        statements = ["BEGIN;", "SELECT pg_advisory_xact_lock(67131315);", schema]
        # Reject ALL pre-existing target rows before importing anything.
        for table in TABLES:
            statements.append(f"DO $$ BEGIN IF EXISTS (SELECT 1 FROM {table}) THEN RAISE EXCEPTION 'Target table {table} must be empty'; END IF; END $$;")
        counts = {}
        available = {row[0].lower() for row in snapshot.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for table in TABLES:
            if table.lower() not in available and table in TABLES[:7]:
                raise ValueError(f"Missing required source table: {table}")
            # Earlier versions may lack the wallet and demo payment tables.
            cursor = snapshot.execute(f'SELECT * FROM "{table}"') if table.lower() in available else None
            columns = [column[0] for column in cursor.description] if cursor else []
            if table == "DamageReports" and "BookingId" in columns and "UserId" not in columns:
                cursor = snapshot.execute("SELECT d.*,b.UserId,b.CourtName AS Subject FROM DamageReports d JOIN Bookings b ON b.Id=d.BookingId")
                columns = [column[0] for column in cursor.description]
            # Names come from this app's schema, not user input.
            if any(not re.fullmatch(r"[A-Za-z][A-Za-z0-9]*", column) for column in columns):
                raise ValueError(f"Unexpected column name in {table}")
            counts[table] = 0
            for row in cursor if cursor is not None else ():
                statements.append(f"INSERT INTO {table}({','.join(columns)}) VALUES({','.join(map(literal, row))});")
                counts[table] += 1
            if table not in ("BookedSlots", "BlockedSlots", "MembershipPlans", "DemoPayments", "RentalEquipment", "CourtReviews", "PaymentReceipts", "DamageClaims", "DamageReceipts", "EquipmentStock", "EquipmentLoans", "BookingEntryPasses", "BookingCheckIns", "BookingCheckOuts"):
                statements.append(f"SELECT setval(pg_get_serial_sequence('{table.lower()}','id'),COALESCE((SELECT MAX(Id) FROM {table}),1),EXISTS(SELECT 1 FROM {table}));")
            statements.append(f"DO $$ BEGIN IF (SELECT COUNT(*) FROM {table}) <> {counts[table]} THEN RAISE EXCEPTION 'Row count mismatch: {table}'; END IF; END $$;")
        statements.append("COMMIT;")
        # Exclusive create also protects against overwriting between validation and write.
        with output.open("x", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(statements) + "\n")
        return counts


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    for table, count in export(args.source, args.output).items():
        print(f"{table}: {count} rows")
