"""Run: python tests/export_smoke.py. No external dependencies required."""
import base64
from contextlib import closing
import importlib.util
from pathlib import Path
import sqlite3
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("export_sqlite", ROOT / "Database/export_sqlite.py")
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


def run():
    with tempfile.TemporaryDirectory(prefix="courtbook-export-") as directory:
        source = Path(directory) / "source.db"
        output = Path(directory) / "test.migration.sql"
        text = "ไทย O'Brien \\ test\nnext line"
        binary = b"\x00\xff\x27\x5c"
        # Small fixture tests export fidelity independently of application seeding.
        with closing(sqlite3.connect(source)) as connection:
            for table in exporter.TABLES:
                connection.execute(f"CREATE TABLE {table}(Id INTEGER PRIMARY KEY, Note TEXT, Data BLOB)")
            connection.execute("INSERT INTO Users VALUES(?,?,?)", (17, text, binary))
            connection.execute("INSERT INTO Courts VALUES(?,?,?)", (42, None, None))
            connection.commit()
        before = source.read_bytes()
        counts = exporter.export(source, output)
        sql = output.read_text(encoding="utf-8")
        assert source.read_bytes() == before, "Export modified the source database"
        assert counts["Users"] == counts["Courts"] == 1 and sum(counts.values()) == 2
        assert "VALUES(17," in sql and "VALUES(42,NULL,NULL)" in sql, "IDs or nulls changed"
        assert base64.b64encode(text.encode()).decode() in sql, "Unicode/quotes/newlines lost"
        assert exporter.literal(binary) in sql, "Image bytes changed"
        assert "Target table Users must be empty" in sql, "Nonempty target is not guarded"
        assert "INSERT INTO MembershipPlans VALUES" not in sql, "New-project seeds leak into migration"
        assert "COALESCE((SELECT MAX(Id) FROM Users),1)" in sql, "Identity not reset"
        assert sql.startswith("BEGIN;") and sql.rstrip().endswith("COMMIT;")
        try:
            exporter.export(source, output)
        except ValueError:
            pass
        else:
            raise AssertionError("Existing output was overwritten")
        assert exporter.literal(None) == "NULL" and exporter.literal(-125) == "-125"
        missing = Path(directory) / "missing.db"
        try:
            exporter.export(missing, Path(directory) / "missing.sql")
        except ValueError:
            pass
        else:
            raise AssertionError("Missing database was accepted")
        assert not missing.exists(), "Missing source was created"
        with closing(sqlite3.connect(source)) as connection:
            for table in exporter.TABLES[7:]:
                connection.execute(f"DROP TABLE {table}")
            connection.commit()
        legacy_counts = exporter.export(source, Path(directory) / "legacy.sql")
        assert legacy_counts["Users"] == 1 and all(legacy_counts[t] == 0 for t in exporter.TABLES[7:]), "Pre-wallet database not supported"
        with closing(sqlite3.connect(source)) as connection:
            connection.execute("ALTER TABLE Bookings ADD COLUMN UserId INTEGER")
            connection.execute("ALTER TABLE Bookings ADD COLUMN CourtName TEXT")
            connection.execute("INSERT INTO Bookings(Id,UserId,CourtName) VALUES(7,17,'Legacy court')")
            connection.execute("CREATE TABLE DamageReports(Id INTEGER PRIMARY KEY,BookingId INTEGER NOT NULL,Category TEXT,Description TEXT)")
            connection.execute("INSERT INTO DamageReports VALUES(9,7,'Court','Legacy damage')")
            connection.commit()
        legacy_before=source.read_bytes()
        upgraded=Path(directory)/'legacy-reports.sql'
        legacy_reports=exporter.export(source,upgraded)
        migration=upgraded.read_text(encoding='utf-8')
        assert legacy_reports['DamageReports']==1 and 'INSERT INTO DamageReports(Id,BookingId,Category,Description,UserId,Subject)' in migration
        assert exporter.literal('Legacy court') in migration and source.read_bytes()==legacy_before, 'Legacy report owner/subject export changed source or lost data'
    print("PASS: snapshot export preserves values/source, guards target/output, resets IDs, and wraps one transaction")


if __name__ == "__main__":
    run()
