using System.Data.Common;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeWalletCheckout(DbConnection c)
    {
        if(c is Npgsql.NpgsqlConnection)return;
        using var definition=Cmd(c,"SELECT sql FROM sqlite_master WHERE type='table' AND name='TopUps'");
        var schema=(string)definition.ExecuteScalar()!;
        var hasPath=schema.Contains("CheckoutPath",StringComparison.OrdinalIgnoreCase);
        var oldMinimum=System.Text.RegularExpressions.Regex.IsMatch(schema,@"AmountSatang\s+BETWEEN\s+100\s+AND",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!oldMinimum&&hasPath)return;
        // SQLite needs a table rebuild to change CHECK. Keep IDs and all referencing wallet entries.
        using var disable=Cmd(c,"PRAGMA foreign_keys=OFF");disable.ExecuteNonQuery();
        try
        {
            using var tx=BeginWrite(c);
            if(oldMinimum)
            {
                using var create=Cmd(c,"""
                    CREATE TABLE TopUps_checkout (
                      Id INTEGER PRIMARY KEY,UserId INTEGER NOT NULL REFERENCES Users(Id),
                      AmountSatang INTEGER NOT NULL CHECK(AmountSatang BETWEEN 1 AND 10000000),
                      TransferReference TEXT COLLATE NOCASE NOT NULL UNIQUE,SlipData BLOB NOT NULL,SlipContentType TEXT NOT NULL,SlipHash TEXT NOT NULL UNIQUE,
                      Status TEXT NOT NULL DEFAULT 'Pending' CHECK(Status IN ('Pending','Approved','Rejected')),
                      CreatedAt TEXT NOT NULL,ReviewedAt TEXT,ReviewedBy INTEGER REFERENCES Users(Id),ReviewNote TEXT NOT NULL DEFAULT '',CheckoutPath TEXT NOT NULL DEFAULT '');
                    """,tx);create.ExecuteNonQuery();
                var columns="Id,UserId,AmountSatang,TransferReference,SlipData,SlipContentType,SlipHash,Status,CreatedAt,ReviewedAt,ReviewedBy,ReviewNote";
                using var copy=Cmd(c,$"INSERT INTO TopUps_checkout({columns},CheckoutPath) SELECT {columns},{(hasPath?"CheckoutPath":"''")} FROM TopUps; DROP TABLE TopUps; ALTER TABLE TopUps_checkout RENAME TO TopUps",tx);copy.ExecuteNonQuery();
            }
            else
            {
                using var column=Cmd(c,"ALTER TABLE TopUps ADD COLUMN CheckoutPath TEXT NOT NULL DEFAULT ''",tx);column.ExecuteNonQuery();
            }
            using var index=Cmd(c,"CREATE UNIQUE INDEX IF NOT EXISTS IX_TopUps_Reference_Lower ON TopUps(lower(TransferReference))",tx);index.ExecuteNonQuery();
            using var check=Cmd(c,"PRAGMA foreign_key_check",tx);using var r=check.ExecuteReader();
            if(r.Read())throw new InvalidOperationException("Wallet upgrade would leave invalid relationships.");
            r.Close();tx.Commit();
        }
        finally {using var enable=Cmd(c,"PRAGMA foreign_keys=ON");enable.ExecuteNonQuery();}
    }
}
