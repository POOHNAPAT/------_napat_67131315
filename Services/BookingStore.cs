using System.Globalization;
using CourtBook.Models;
using Microsoft.AspNetCore.Identity;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CourtBook.Services;

public partial class BookingStore
{
    private readonly string connectionString;
    private readonly bool postgres;
    private readonly PasswordHasher<SiteUser> hasher = new();
    public static readonly string[] Rules = ["NewMember", "Consecutive", "WeekdayMorning", "Advance", "Threshold", "Group"];

    public BookingStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        postgres = provider.Equals("Supabase", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
        if (!postgres && !provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database:Provider must be Sqlite, Supabase or Postgres.");
        if (postgres)
        {
            connectionString = configuration.GetConnectionString("Supabase")
                ?? throw new InvalidOperationException("Set ConnectionStrings__Supabase before using Supabase.");
        }
        else
        {
            var path = Path.GetFullPath(configuration["Database:Path"] ?? "App_Data/courtbook.db", environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 15 }.ToString();
        }
        using var c = Open();
        if (postgres)
        {
            using var stream = typeof(BookingStore).Assembly.GetManifestResourceStream("CourtBook.Database.supabase.sql")!;
            using var reader = new StreamReader(stream);
            using var tx = BeginWrite(c);
            using var init = Cmd(c, reader.ReadToEnd(), tx);
            init.ExecuteNonQuery();
            tx.Commit();
        }
        else
        {
            using var schema = Cmd(c, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Users (
                  Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Email TEXT COLLATE NOCASE NOT NULL UNIQUE,
                  PasswordHash TEXT NOT NULL, Role TEXT NOT NULL CHECK(Role IN ('Member','Admin')));
                CREATE TABLE IF NOT EXISTS Courts (
                  Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL,
                  PriceSatang INTEGER NOT NULL CHECK(PriceSatang > 0), IsActive INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS Promotions (
                  Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Rule TEXT NOT NULL, Kind TEXT NOT NULL,
                  Value100 INTEGER NOT NULL, MinHours INTEGER NOT NULL, MinDays INTEGER NOT NULL,
                  MinSpendSatang INTEGER NOT NULL, Starts TEXT NOT NULL, Ends TEXT NOT NULL, IsActive INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS Bookings (
                  Id INTEGER PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES Users(Id), CourtId INTEGER NOT NULL REFERENCES Courts(Id),
                  CourtName TEXT NOT NULL, PlayDate TEXT NOT NULL, StartHour INTEGER NOT NULL, Hours INTEGER NOT NULL,
                  PriceSatang INTEGER NOT NULL, GrossSatang INTEGER NOT NULL, DiscountSatang INTEGER NOT NULL, NetSatang INTEGER NOT NULL,
                  PromotionId INTEGER REFERENCES Promotions(Id), PromotionName TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                  Status TEXT NOT NULL DEFAULT 'Confirmed');
                CREATE TABLE IF NOT EXISTS BookedSlots (
                  CourtId INTEGER NOT NULL REFERENCES Courts(Id), PlayDate TEXT NOT NULL, Hour INTEGER NOT NULL,
                  BookingId INTEGER NOT NULL REFERENCES Bookings(Id), PRIMARY KEY(CourtId, PlayDate, Hour));
                CREATE TABLE IF NOT EXISTS BlockedSlots (
                  CourtId INTEGER NOT NULL REFERENCES Courts(Id), PlayDate TEXT NOT NULL, Hour INTEGER NOT NULL,
                  PRIMARY KEY(CourtId, PlayDate, Hour));
                CREATE INDEX IF NOT EXISTS IX_Bookings_User ON Bookings(UserId);
                """);
            schema.ExecuteNonQuery();
            UpgradeSchema(c);
            InitializeWallet(c);
        }
        Seed(c, configuration.GetValue<bool>("Demo:SeedAccounts"));
        BootstrapAdmin(c,configuration);
        InitializeEquipment(c);
        InitializeProductFeatures(c);
        InitializeDamageReports(c);
        InitializeMembershipQuotas(c);
        InitializeReceipts(c);
        InitializeAccounting(c);
    }

    private static void UpgradeSchema(DbConnection c)
    {
        // Additive, atomic and repeatable: existing confirmations and booking slots are preserved.
        using var tx = BeginWrite(c);
        using var types = Cmd(c, """
            CREATE TABLE IF NOT EXISTS SportTypes (Id INTEGER PRIMARY KEY, Name TEXT COLLATE NOCASE NOT NULL UNIQUE);
            INSERT OR IGNORE INTO SportTypes(Id,Name) VALUES(1,'แบดมินตัน');
            """, tx);
        types.ExecuteNonQuery();
        void AddColumn(string table, string name, string definition)
        {
            using var info = Cmd(c, $"PRAGMA table_info({table})", tx);
            using var reader = info.ExecuteReader();
            var exists = false;
            while (reader.Read()) exists |= reader.GetString(1) == name;
            reader.Close();
            if (exists) return;
            using var alter = Cmd(c, $"ALTER TABLE {table} ADD COLUMN {name} {definition}", tx);
            alter.ExecuteNonQuery();
        }
        // SQLite requires a nullable default when adding a foreign key to populated tables.
        AddColumn("Courts", "SportTypeId", "INTEGER REFERENCES SportTypes(Id)");
        AddColumn("Courts", "ImageData", "BLOB");
        AddColumn("Courts", "ImageContentType", "TEXT");
        AddColumn("Courts", "ImageVersion", "TEXT NOT NULL DEFAULT ''");
        AddColumn("Courts", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
        using var defaults = Cmd(c, "UPDATE Courts SET SportTypeId=1 WHERE SportTypeId IS NULL", tx);
        defaults.ExecuteNonQuery();
        AddColumn("Bookings", "ConfirmedAt", "TEXT");
        AddColumn("Bookings", "CancelledAt", "TEXT");
        AddColumn("Bookings", "ConfirmedBy", "INTEGER REFERENCES Users(Id)");
        AddColumn("Bookings", "PaidAt", "TEXT");
        AddColumn("Bookings", "PaidBy", "INTEGER REFERENCES Users(Id)");
        AddColumn("Bookings", "PaymentReference", "TEXT NOT NULL DEFAULT ''");
        AddColumn("Bookings", "PaymentSubmittedAt", "TEXT");
        AddColumn("Bookings", "TransferReference", "TEXT NOT NULL DEFAULT ''");
        AddColumn("Bookings", "SlipData", "BLOB");
        AddColumn("Bookings", "SlipContentType", "TEXT");
        AddColumn("Bookings", "EquipmentJson", "TEXT NOT NULL DEFAULT '[]'");
        tx.Commit();
    }

    public IReadOnlyList<SportType> SportTypes()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,Name FROM SportTypes ORDER BY Id");
        using var r = cmd.ExecuteReader();
        var result = new List<SportType>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1)));
        return result;
    }
    public void AddSportType(SportTypeForm form)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO SportTypes(Name) VALUES($name)", null, ("$name", form.Name.Trim()));
        try { cmd.ExecuteNonQuery(); InitializeProductFeatures(c); }
        catch (DbException ex) when (IsUniqueViolation(ex))
        { throw new BookingRuleException("ประเภทกีฬานี้มีอยู่แล้ว"); }
    }
    public IReadOnlyList<CustomerAccount> Customers()
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT u.Id,u.Name,u.Email,COUNT(b.Id) FROM Users u LEFT JOIN Bookings b ON b.UserId=u.Id
            WHERE u.Role='Member' GROUP BY u.Id ORDER BY u.Id DESC
            """);
        using var r = cmd.ExecuteReader();
        var result = new List<CustomerAccount>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), Convert.ToInt32(r.GetValue(3))));
        return result;
    }
    public bool ConfirmBooking(long id, long adminId)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            UPDATE Bookings SET Status='Confirmed',ConfirmedAt=$now,ConfirmedBy=$admin
            WHERE Id=$id AND Status='Pending' AND EXISTS(SELECT 1 FROM Users WHERE Id=$admin AND Role='Admin')
            """, null, ("$id", id), ("$admin", adminId), ("$now", DateTime.UtcNow.ToString("O")));
        return cmd.ExecuteNonQuery() == 1;
    }
    public bool CancelBooking(long id, long userId)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        using var find = Cmd(c, "SELECT PlayDate,StartHour,Status,PaidAt,PaymentSubmittedAt,NetSatang,COALESCE((SELECT Hours FROM MembershipQuotaUses WHERE BookingId=Bookings.Id),0) FROM Bookings WHERE Id=$id AND UserId=$user",
            tx, ("$id", id), ("$user", userId));
        using var r = find.ExecuteReader();
        if (!r.Read()) return false;
        var start = Date(r.GetString(0)).ToDateTime(new TimeOnly(r.GetInt32(1), 0));
        if (r.GetString(2) is not ("Pending" or "Confirmed"))
            throw new BookingRuleException("รายการนี้ถูกยกเลิกแล้ว");
        if (!r.IsDBNull(3) && !(r.GetInt64(5)==0 && r.GetInt32(6)>0)) throw new BookingRuleException("รายการนี้ยืนยันชำระเงินแล้ว กรุณาติดต่อผู้ดูแลเพื่อจัดการยกเลิกและคืนเงิน");
        if (!r.IsDBNull(4)) throw new BookingRuleException("รายการนี้แจ้งโอนเงินแล้ว กรุณาติดต่อผู้ดูแลเพื่อจัดการยกเลิกและตรวจสอบการคืนเงิน");
        if (start <= CourtTime.Now) throw new BookingRuleException("ไม่สามารถยกเลิกรายการที่ถึงเวลาเล่นแล้ว");
        r.Close();
        using var update = Cmd(c, "UPDATE Bookings SET Status='Cancelled',CancelledAt=$now WHERE Id=$id", tx,
            ("$id", id), ("$now", DateTime.UtcNow.ToString("O")));
        update.ExecuteNonQuery();
        using var release = Cmd(c, "DELETE FROM BookedSlots WHERE BookingId=$id", tx, ("$id", id));
        release.ExecuteNonQuery();
        tx.Commit();
        return true;
    }
    public bool ConfirmPayment(long id, long adminId, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 200)
            throw new BookingRuleException("กรุณาระบุเลขอ้างอิงหรือรายละเอียดการรับเงินไม่เกิน 200 ตัวอักษร");
        using var c = Open();
        using var tx = BeginWrite(c);
        using var cmd = Cmd(c, """
            UPDATE Bookings SET PaidAt=$now,PaidBy=$admin,PaymentReference=$reference
            WHERE Id=$id AND Status='Confirmed' AND PaidAt IS NULL
            AND EXISTS(SELECT 1 FROM Users WHERE Id=$admin AND Role='Admin')
            """, tx, ("$id", id), ("$admin", adminId), ("$reference", reference.Trim()), ("$now", DateTime.UtcNow.ToString("O")));
        var updated=cmd.ExecuteNonQuery()==1;
        if(updated)IssueBookingReceipt(c,tx,id);
        tx.Commit();return updated;
    }
    public bool SubmitPayment(long id, long userId, PaymentSubmissionForm form)
    {
        if (form.TransferReference?.Trim().Length > 200)
            throw new BookingRuleException("เลขรายการโอนต้องไม่เกิน 200 ตัวอักษร");
        var slip = ReadUploadedImage(form.Slip) ?? throw new BookingRuleException("กรุณาแนบรูปสลิปโอนเงิน");
        var reference = string.IsNullOrWhiteSpace(form.TransferReference)
            ? "SLIP-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(slip.Data))
            : form.TransferReference.Trim();
        using var c = Open();
        using var tx = BeginWrite(c);
        using var walletProof = Cmd(c, "SELECT COUNT(*) FROM TopUps WHERE lower(TransferReference)=lower($reference) OR SlipHash=$hash", tx,
            ("$reference", reference), ("$hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(slip.Data))));
        if ((long)walletProof.ExecuteScalar()! > 0) throw new BookingRuleException("สลิปหรือเลขโอนนี้เคยใช้เติมเงินแล้ว");
        using var cmd = Cmd(c, """
            UPDATE Bookings SET PaymentSubmittedAt=$now,TransferReference=$reference,SlipData=$slip,SlipContentType=$mime
            WHERE Id=$id AND UserId=$user AND Status IN ('Pending','Confirmed') AND PaidAt IS NULL AND NetSatang>0
            """, tx, ("$id", id), ("$user", userId), ("$now", DateTime.UtcNow.ToString("O")),
            ("$reference", reference), ("$slip", slip.Data), ("$mime", slip.ContentType));
        var updated = cmd.ExecuteNonQuery() == 1;
        tx.Commit();
        return updated;
    }
    public CourtImage? FindPaymentSlip(long id, long? userId = null)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT SlipData,SlipContentType FROM Bookings WHERE Id=$id AND ($user IS NULL OR UserId=$user) AND SlipData IS NOT NULL",
            null, ("$id", id), ("$user", userId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new CourtImage((byte[])r[0], r.GetString(1)) : null;
    }

    private DbConnection Open()
    {
        DbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static DbTransaction BeginWrite(DbConnection c)
    {
        if (c is SqliteConnection sqlite) return sqlite.BeginTransaction(deferred: false);
        var tx = c.BeginTransaction();
        try
        {
            // ponytail: serialize write transactions for SQLite parity; use per-resource locks if throughput requires it.
            using var guard = Cmd(c, "SELECT pg_advisory_xact_lock(67131315)", tx);
            guard.ExecuteNonQuery();
            return tx;
        }
        catch { tx.Dispose(); throw; }
    }

    private static bool IsUniqueViolation(DbException ex) =>
        ex is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 }
        || ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static DbCommand Cmd(DbConnection c, string sql, DbTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        // Npgsql uses @name parameters; SQLite accepts both @name and $name.
        if (c is NpgsqlConnection)
            sql = System.Text.RegularExpressions.Regex.Replace(sql, @"\$(\w+)", "@$1")
                .Replace("COALESCE(SUM(AmountSatang),0)", "CAST(COALESCE(SUM(AmountSatang),0) AS BIGINT)");
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = c is NpgsqlConnection ? name.Replace('$', '@') : name;
            parameter.Value = value is bool flag ? (flag ? 1 : 0) : value ?? DBNull.Value;
            // Nullable filters must have a type even when their first occurrence is IS NULL.
            if (value is null && parameter is NpgsqlParameter pg)
                pg.NpgsqlDbType = name is "$image" or "$slip" ? NpgsqlTypes.NpgsqlDbType.Bytea
                    : name is "$date" or "$mime" ? NpgsqlTypes.NpgsqlDbType.Text : NpgsqlTypes.NpgsqlDbType.Bigint;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static long Satang(decimal amount) => checked((long)decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero));

    private void Seed(DbConnection c, bool demo)
    {
        using var tx = BeginWrite(c);
        using var courts = Cmd(c, "SELECT COUNT(*) FROM Courts", tx);
        if ((long)courts.ExecuteScalar()! == 0)
        {
            using var insert = Cmd(c, """
                INSERT INTO Courts(Name,Description,PriceSatang,IsActive,SportTypeId) VALUES
                ('สนาม A','พื้นสนามมาตรฐาน พร้อมตาข่ายและไฟส่องสว่าง',20000,1,1),
                ('สนาม B','สนามในร่ม โปร่งสบาย เหมาะกับการเล่นเป็นกลุ่ม',20000,1,1);
                """, tx);
            insert.ExecuteNonQuery();
        }
        using var promotions = Cmd(c, "SELECT COUNT(*) FROM Promotions", tx);
        if ((long)promotions.ExecuteScalar()! == 0)
        {
            foreach (var (name, rule, kind, value) in new[] {
                ("สมาชิกใหม่", "NewMember", "Fixed", 5000), ("จองต่อเนื่อง", "Consecutive", "Percent", 1500),
                ("ช่วงเช้าวันธรรมดา", "WeekdayMorning", "Percent", 1000), ("จองล่วงหน้า", "Advance", "Fixed", 8000),
                ("ยอดจองครบ", "Threshold", "Fixed", 10000) })
            {
                using var insert = Cmd(c, """
                    INSERT INTO Promotions(Name,Rule,Kind,Value100,MinHours,MinDays,MinSpendSatang,Starts,Ends,IsActive)
                    VALUES($name,$rule,$kind,$value,3,7,100000,$starts,$ends,1)
                    """, tx, ("$name", name), ("$rule", rule), ("$kind", kind), ("$value", value),
                    ("$starts", CourtTime.Iso(CourtTime.Today)), ("$ends", CourtTime.Iso(CourtTime.Today.AddYears(1))));
                insert.ExecuteNonQuery();
            }
        }
        using var users = Cmd(c, "SELECT COUNT(*) FROM Users", tx);
        if (demo && (long)users.ExecuteScalar()! == 0)
        {
            InsertUser(c, tx, "ผู้ดูแล COURTBOOK", "admin@courtbook.local", "CourtBook123!", "Admin");
            InsertUser(c, tx, "สมาชิกตัวอย่าง", "member@courtbook.local", "Member123!", "Member");
        }
        tx.Commit();
    }

    private long InsertUser(DbConnection c, DbTransaction? tx, string name, string email, string password, string role)
    {
        var user = new SiteUser { Name = name.Trim(), Email = email.Trim(), Role = role };
        using var cmd = Cmd(c, """
            INSERT INTO Users(Name,Email,PasswordHash,Role) VALUES($name,$email,$hash,$role) RETURNING Id
            """, tx, ("$name", user.Name), ("$email", user.Email), ("$hash", hasher.HashPassword(user, password)), ("$role", role));
        return (long)cmd.ExecuteScalar()!;
    }
    public bool Register(RegisterForm form)
    {
        using var c = Open();
        try { InsertUser(c, null, form.Name, form.Email, form.Password, "Member"); return true; }
        catch (DbException ex) when (IsUniqueViolation(ex)) { return false; }
    }
    private static SiteUser ReadUser(DbDataReader r) => new()
        { Id = r.GetInt64(0), Name = r.GetString(1), Email = r.GetString(2), PasswordHash = r.GetString(3), Role = r.GetString(4) };
    public SiteUser? FindUser(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,Name,Email,PasswordHash,Role FROM Users WHERE Id=$id AND IsLocked=0", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }
    public SiteUser? Authenticate(string email, string password)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,Name,Email,PasswordHash,Role FROM Users WHERE lower(Email)=lower($email) AND IsLocked=0", null, ("$email", email.Trim()));
        using var r = cmd.ExecuteReader();
        var user = r.Read() ? ReadUser(r) : null;
        return user is not null && hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed ? user : null;
    }

    private static Court ReadCourt(DbDataReader r) => new()
        { Id = r.GetInt64(0), Name = r.GetString(1), Description = r.GetString(2), PriceSatang = r.GetInt64(3), IsActive = (Convert.ToInt32(r.GetValue(4)) != 0), SportTypeId = r.GetInt64(5), SportTypeName = r.GetString(6), ImageVersion = r.GetString(7), Location=r.GetString(8), Rating=r.IsDBNull(9)?null:Convert.ToDecimal(r.GetValue(9)), ReviewCount=Convert.ToInt32(r.GetValue(10)) };
    private static Court? GetCourt(DbConnection c, long id, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, "SELECT c.Id,c.Name,c.Description,c.PriceSatang,c.IsActive,c.SportTypeId,s.Name,c.ImageVersion,c.Location,(SELECT AVG(CAST(v.Rating AS REAL)) FROM CourtReviews v WHERE v.CourtId=c.Id),(SELECT COUNT(*) FROM CourtReviews v WHERE v.CourtId=c.Id) FROM Courts c JOIN SportTypes s ON s.Id=c.SportTypeId WHERE c.Id=$id AND c.IsDeleted=0", tx, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCourt(r) : null;
    }
    public Court? FindCourt(long id) { using var c = Open(); return GetCourt(c, id); }
    public IReadOnlyList<Court> Courts(bool includeClosed = false)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT c.Id,c.Name,c.Description,c.PriceSatang,c.IsActive,c.SportTypeId,s.Name,c.ImageVersion,c.Location,(SELECT AVG(CAST(v.Rating AS REAL)) FROM CourtReviews v WHERE v.CourtId=c.Id),(SELECT COUNT(*) FROM CourtReviews v WHERE v.CourtId=c.Id) FROM Courts c JOIN SportTypes s ON s.Id=c.SportTypeId WHERE c.IsDeleted=0 AND (c.IsActive=1 OR $all=1) ORDER BY c.Id", null, ("$all", includeClosed));
        using var r = cmd.ExecuteReader();
        var result = new List<Court>();
        while (r.Read()) result.Add(ReadCourt(r));
        return result;
    }
    public bool SaveCourt(CourtForm form)
    {
        var image = ReadUploadedImage(form.Image);
        var replaceImage = image is not null || form.RemoveImage;
        using var c = Open();
        using var type = Cmd(c, "SELECT COUNT(*) FROM SportTypes WHERE Id=$id", null, ("$id", form.SportTypeId));
        if ((long)type.ExecuteScalar()! == 0) throw new BookingRuleException("ไม่พบประเภทกีฬาที่เลือก");
        using var cmd = Cmd(c, form.Id == 0
            ? "INSERT INTO Courts(Name,Description,PriceSatang,IsActive,SportTypeId,ImageData,ImageContentType,ImageVersion,Location) VALUES($name,$description,$price,$active,$type,$image,$mime,$version,$location)"
            : "UPDATE Courts SET Location=$location,Name=$name,Description=$description,PriceSatang=$price,IsActive=$active,SportTypeId=$type,ImageData=CASE WHEN $replace=1 THEN $image ELSE ImageData END,ImageContentType=CASE WHEN $replace=1 THEN $mime ELSE ImageContentType END,ImageVersion=CASE WHEN $replace=1 THEN $version ELSE ImageVersion END WHERE Id=$id AND IsDeleted=0",
            null, ("$id", form.Id), ("$location",form.Location?.Trim()??""), ("$name", form.Name.Trim()), ("$description", form.Description?.Trim() ?? ""),
            ("$price", Satang(form.Price)), ("$active", form.IsActive), ("$type", form.SportTypeId),
            ("$replace", replaceImage), ("$image", image?.Data), ("$mime", image?.ContentType), ("$version", image is null ? "" : Guid.NewGuid().ToString("N")));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool DeleteCourt(long id)
    {
        // Keep the court row so existing bookings retain their foreign key and history.
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE Courts SET IsDeleted=1,IsActive=0 WHERE Id=$id AND IsDeleted=0", null, ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static CourtImage? ReadUploadedImage(IFormFile? file)
    {
        if (file is null) return null;
        if (file.Length == 0 || file.Length > 5 * 1024 * 1024)
            throw new BookingRuleException("กรุณาเลือกรูปขนาดมากกว่า 0 และไม่เกิน 5 MB");
        using var stream = file.OpenReadStream();
        var data = new byte[(int)file.Length];
        stream.ReadExactly(data);
        // Determine the response type from file bytes, never from the supplied name or MIME type.
        var bytes = data.AsSpan();
        var mime = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
            : null;
        if (mime is null) throw new BookingRuleException("รองรับเฉพาะรูป JPG, PNG หรือ WebP กรุณาเลือกไฟล์รูปภาพใหม่");
        return new CourtImage(data, mime);
    }

    public CourtImage? FindCourtImage(long id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT ImageData,ImageContentType FROM Courts WHERE Id=$id AND IsDeleted=0 AND ImageData IS NOT NULL", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new CourtImage((byte[])r[0], r.GetString(1)) : null;
    }

    private static IReadOnlyList<Promotion> GetPromotions(DbConnection c, bool all, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, """
            SELECT Id,Name,Rule,Kind,Value100,MinHours,MinDays,MinSpendSatang,Starts,Ends,IsActive,MinPlayers FROM Promotions
            WHERE $all=1 OR (IsActive=1 AND Starts <= $today AND Ends >= $today) ORDER BY Id
            """, tx, ("$all", all), ("$today", CourtTime.Iso(CourtTime.Today)));
        using var r = cmd.ExecuteReader();
        var result = new List<Promotion>();
        while (r.Read()) result.Add(new Promotion {
            Id=r.GetInt64(0), Name=r.GetString(1), Rule=r.GetString(2), Kind=r.GetString(3), Value100=r.GetInt32(4),
            MinHours=r.GetInt32(5), MinDays=r.GetInt32(6), MinSpendSatang=r.GetInt64(7), Starts=Date(r.GetString(8)),
            MinPlayers=r.GetInt32(11),Ends=Date(r.GetString(9)), IsActive=(Convert.ToInt32(r.GetValue(10)) != 0) });
        return result;
    }
    public IReadOnlyList<Promotion> Promotions(bool all = false) { using var c = Open(); return GetPromotions(c, all); }
    public bool SavePromotion(PromotionForm form)
    {
        if (!Rules.Contains(form.Rule) || !new[] { "Fixed", "Percent" }.Contains(form.Kind)
            || form.Starts > form.Ends || (form.Kind == "Percent" && form.Value > 100))
            throw new BookingRuleException("ตรวจสอบเงื่อนไข ประเภทส่วนลด ช่วงวันที่ และส่วนลดเปอร์เซ็นต์ต้องไม่เกิน 100%");
        using var c = Open();
        using var cmd = Cmd(c, form.Id == 0 ? """
            INSERT INTO Promotions(Name,Rule,Kind,Value100,MinHours,MinDays,MinSpendSatang,Starts,Ends,IsActive,MinPlayers)
            VALUES($name,$rule,$kind,$value,$hours,$days,$spend,$starts,$ends,$active,$players)
            """ : """
            UPDATE Promotions SET MinPlayers=$players,Name=$name,Rule=$rule,Kind=$kind,Value100=$value,MinHours=$hours,MinDays=$days,
            MinSpendSatang=$spend,Starts=$starts,Ends=$ends,IsActive=$active WHERE Id=$id
            """, null, ("$id",form.Id), ("$players",form.MinPlayers), ("$name",form.Name.Trim()), ("$rule",form.Rule), ("$kind",form.Kind),
            ("$value",Satang(form.Value)), ("$hours",form.MinHours), ("$days",form.MinDays), ("$spend",Satang(form.MinSpend)),
            ("$starts",CourtTime.Iso(form.Starts)), ("$ends",CourtTime.Iso(form.Ends)), ("$active",form.IsActive));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static IReadOnlyList<Slot> GetSlots(DbConnection c, Court court, DateOnly date, DbTransaction? tx = null, bool ignoreCourtClosed = false)
    {
        using var cmd = Cmd(c, """
            SELECT Hour,'Booked' FROM BookedSlots WHERE CourtId=$court AND PlayDate=$date
            UNION ALL SELECT Hour,'Closed' FROM BlockedSlots WHERE CourtId=$court AND PlayDate=$date
            """, tx, ("$court",court.Id), ("$date",CourtTime.Iso(date)));
        using var r = cmd.ExecuteReader();
        var occupied = new Dictionary<int,string>();
        while (r.Read()) occupied[r.GetInt32(0)] = r.GetString(1);
        var now = CourtTime.Now;
        return CourtTime.Hours.Select(hour => new Slot(hour, occupied.GetValueOrDefault(hour)
            ?? (!court.IsActive && !ignoreCourtClosed ? "Closed" : date.ToDateTime(TimeOnly.MinValue).AddHours(hour) <= now ? "Past" : "Available"))).ToList();
    }
    public IReadOnlyList<Slot> Slots(Court court, DateOnly date, bool ignoreCourtClosed = false) { using var c = Open(); return GetSlots(c,court,date,null,ignoreCourtClosed); }
    public void SaveSchedule(long courtId, DateOnly date, int[] blocked)
    {
        if (date < CourtTime.Today || blocked.Any(h => !CourtTime.Hours.Contains(h)))
            throw new BookingRuleException("วันที่หรือช่วงเวลาไม่ถูกต้อง ไม่สามารถเปลี่ยนตารางวันย้อนหลังได้");
        using var c = Open();
        using var tx = BeginWrite(c);
        var court = GetCourt(c,courtId,tx) ?? throw new BookingRuleException("ไม่พบสนาม");
        var booked = GetSlots(c,court,date,tx).Where(s => s.Status == "Booked").Select(s => s.Hour).ToHashSet();
        if (blocked.Any(booked.Contains)) throw new BookingRuleException("ช่วงเวลาที่มีการจองแล้วไม่สามารถปิดทับได้");
        using var clear = Cmd(c,"DELETE FROM BlockedSlots WHERE CourtId=$court AND PlayDate=$date",tx,("$court",courtId),("$date",CourtTime.Iso(date)));
        clear.ExecuteNonQuery();
        foreach (var hour in blocked.Distinct())
        {
            using var insert = Cmd(c,"INSERT INTO BlockedSlots(CourtId,PlayDate,Hour) VALUES($court,$date,$hour)",tx,
                ("$court",courtId),("$date",CourtTime.Iso(date)),("$hour",hour));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static Quote Evaluate(DbConnection c, long userId, BookingForm form, out Court court, DbTransaction? tx = null)
    {
        court = GetCourt(c,form.CourtId,tx) ?? throw new BookingRuleException("ไม่พบสนามที่เลือก");
        if (!court.IsActive) throw new BookingRuleException("สนามนี้ปิดให้จอง");
        var hours = form.Hours.Order().ToArray();
        if (form.Date < CourtTime.Today || hours.Length == 0 || hours.Distinct().Count() != hours.Length
            || hours.Any(h => !CourtTime.Hours.Contains(h)) || hours[^1]-hours[0]+1 != hours.Length)
            throw new BookingRuleException("เลือกวันที่ปัจจุบันหรืออนาคต และเลือกชั่วโมงต่อเนื่องอย่างน้อย 1 ชั่วโมง");
        var slots = GetSlots(c,court,form.Date,tx);
        if (hours.Any(h => slots.Single(s=>s.Hour==h).Status != "Available"))
            throw new BookingRuleException("ช่วงเวลานี้ไม่ว่างแล้ว หรือปิดให้จอง กรุณาเลือกเวลาใหม่");
        using var count = Cmd(c,"SELECT COUNT(*) FROM Bookings WHERE UserId=$user AND Status<>'Cancelled'",tx,("$user",userId));
        var first = (long)count.ExecuteScalar()! == 0;
        var equipment = SelectEquipment(c, court.SportTypeId, form.Equipment, tx);
        var equipmentTotal = equipment.Sum(e => e.TotalSatang);
        var gross = checked(court.PriceSatang * hours.Length);
        var promotions = GetPromotions(c,false,tx);
        bool Eligible(Promotion p) => p.Rule switch
        {
            "Group" => form.Players>=p.MinPlayers,
            "NewMember" => first,
            "Consecutive" => hours.Length >= p.MinHours,
            "WeekdayMorning" => form.Date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && hours.All(h=>h>=8 && h<12),
            "Advance" => form.Date.DayNumber - CourtTime.Today.DayNumber >= p.MinDays,
            "Threshold" => gross >= p.MinSpendSatang,
            _ => false
        };
        long discount = 0;
        if (form.PromotionId.HasValue)
        {
            var selected = promotions.SingleOrDefault(p=>p.Id==form.PromotionId.Value);
            if (selected is null || !Eligible(selected)) throw new BookingRuleException("โปรโมชั่นนี้หมดอายุ ปิดใช้งาน หรือไม่ตรงตามเงื่อนไข กรุณาเลือกใหม่");
            discount = Math.Min(gross, selected.Kind == "Percent"
                ? (long)decimal.Round(gross * selected.Value100 / 10000m,0,MidpointRounding.AwayFromZero) : selected.Value100);
        }
        string? membershipName = null;
        var included=0;long? quotaMembershipId=null;var quotaPeriod="";var remaining=0;
        var membership = ActiveMembership(c,userId,tx);
        if (membership is not null && form.Date >= membership.Starts && form.Date < membership.Ends
            && (membership.MonthlyHours==0 || membership.QuotaSportTypeId==court.SportTypeId))
        {
            var period=membership.Quotas?.SingleOrDefault(q=>form.Date>=q.Starts&&form.Date<q.Ends);
            remaining=period?.RemainingHours??0;
            var free=Math.Min(hours.Length,remaining);
            var covered=free*court.PriceSatang;
            var memberDiscount = covered+(long)decimal.Round((gross-covered)*membership.DiscountPercent/100m,0,MidpointRounding.AwayFromZero);
            // Prefer the chosen promotion on ties, preserving the member's quota.
            if (memberDiscount > discount) {
                discount=memberDiscount;membershipName=membership.Name;included=free;
                if(free>0){quotaMembershipId=membership.Id;quotaPeriod=CourtTime.Iso(period!.Starts);}
            }
        }
        return new Quote(gross + equipmentTotal,discount,gross-discount + equipmentTotal,promotions.Select(p=>new PromotionOption(p.Id,p.Name,p.DiscountLabel,Eligible(p),p.Condition)).ToList(),membershipName,equipmentTotal,equipment,included,quotaMembershipId,quotaPeriod,remaining);
    }

    public Quote Preview(long userId, BookingForm form) { using var c = Open(); return Evaluate(c,userId,form,out _); }
    public long Book(long userId, BookingForm form)
    {
        using var c = Open();
        using var tx = BeginWrite(c);
        var quote = Evaluate(c,userId,form,out var court,tx);
        if(form.ExpectedIncludedHours.HasValue && form.ExpectedIncludedHours!=quote.IncludedHours)throw new BookingRuleException("โควตาเปลี่ยนจากรายการจองอื่น กรุณาตรวจยอดใหม่ก่อนยืนยัน");
        var promotion = form.PromotionId.HasValue ? GetPromotions(c,false,tx).Single(p=>p.Id==form.PromotionId) : null;
        using var insert = Cmd(c,"""
            INSERT INTO Bookings(UserId,CourtId,CourtName,PlayDate,StartHour,Hours,PriceSatang,GrossSatang,DiscountSatang,NetSatang,PromotionId,PromotionName,CreatedAt,Status,EquipmentJson,SportTypeName,Players)
            VALUES($user,$court,$name,$date,$start,$hours,$price,$gross,$discount,$net,$promo,$promoName,$created,'Pending',$equipment,$sportName,$players) RETURNING Id
            """,tx,("$user",userId),("$court",court.Id),("$name",court.Name),("$date",CourtTime.Iso(form.Date)),
            ("$start",form.Hours.Min()),("$hours",form.Hours.Length),("$price",court.PriceSatang),("$gross",quote.GrossSatang),
            ("$discount",quote.DiscountSatang),("$net",quote.NetSatang),("$promo",quote.MembershipName is null ? promotion?.Id : null),("$promoName",quote.MembershipName??promotion?.Name??"ไม่ใช้โปรโมชั่น"),
            ("$created",DateTime.UtcNow.ToString("O")),("$equipment",System.Text.Json.JsonSerializer.Serialize(quote.Equipment)),("$sportName",court.SportTypeName),("$players",form.Players));
        var id = (long)insert.ExecuteScalar()!;
        if(quote.IncludedHours>0){
            using var quota=Cmd(c,"INSERT INTO MembershipQuotaUses(MembershipId,BookingId,PeriodStart,Hours) VALUES($membership,$booking,$period,$hours)",tx,
                ("$membership",quote.QuotaMembershipId),("$booking",id),("$period",quote.QuotaPeriod),("$hours",quote.IncludedHours));quota.ExecuteNonQuery();
            if(quote.NetSatang==0){
                using var covered=Cmd(c,"UPDATE Bookings SET PaidAt=$now,PaymentReference=$reference WHERE Id=$id",tx,("$id",id),("$now",DateTime.UtcNow.ToString("O")),("$reference","ใช้โควตาชั่วโมงแพ็กสมาชิก"));covered.ExecuteNonQuery();
                IssueBookingReceipt(c,tx,id);
            }
        }
        foreach (var hour in form.Hours)
        {
            using var slot = Cmd(c,"INSERT INTO BookedSlots(CourtId,PlayDate,Hour,BookingId) VALUES($court,$date,$hour,$id)",tx,
                ("$court",court.Id),("$date",CourtTime.Iso(form.Date)),("$hour",hour),("$id",id));
            slot.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    public IReadOnlyList<Booking> Bookings(long? userId = null, DateOnly? date = null, long? courtId = null)
    {
        using var c = Open();
        using var cmd = Cmd(c,"""
            SELECT b.Id,b.UserId,u.Name,u.Email,b.CourtId,b.CourtName,b.PlayDate,b.StartHour,b.Hours,b.PriceSatang,
            b.GrossSatang,b.DiscountSatang,b.NetSatang,b.PromotionName,b.Status,b.ConfirmedAt,COALESCE(a.Name,''),
            b.PaidAt,COALESCE(p.Name,''),b.PaymentReference,b.CancelledAt,b.PaymentSubmittedAt,b.TransferReference,b.EquipmentJson,b.SportTypeName,b.Players,COALESCE((SELECT Hours FROM MembershipQuotaUses WHERE BookingId=b.Id),0) FROM Bookings b JOIN Users u ON u.Id=b.UserId
            LEFT JOIN Users a ON a.Id=b.ConfirmedBy LEFT JOIN Users p ON p.Id=b.PaidBy
            WHERE ($user IS NULL OR b.UserId=$user) AND ($date IS NULL OR b.PlayDate=$date) AND ($court IS NULL OR b.CourtId=$court)
            ORDER BY b.Id DESC
            """,null,("$user",userId),("$date",date.HasValue?CourtTime.Iso(date.Value):null),("$court",courtId));
        using var r=cmd.ExecuteReader();
        var result=new List<Booking>();
        while(r.Read()) result.Add(new Booking {
            Id=r.GetInt64(0),UserId=r.GetInt64(1),MemberName=r.GetString(2),MemberEmail=r.GetString(3),CourtId=r.GetInt64(4),
            CourtName=r.GetString(5),Date=Date(r.GetString(6)),StartHour=r.GetInt32(7),Hours=r.GetInt32(8),PriceSatang=r.GetInt64(9),
            GrossSatang=r.GetInt64(10),DiscountSatang=r.GetInt64(11),NetSatang=r.GetInt64(12),PromotionName=r.GetString(13),
            ApprovalStatus=r.GetString(14),ConfirmedAt=r.IsDBNull(15)?null:r.GetString(15),ConfirmedByName=r.GetString(16),
            PaidAt=r.IsDBNull(17)?null:r.GetString(17),PaidByName=r.GetString(18),PaymentReference=r.GetString(19),
            CancelledAt=r.IsDBNull(20)?null:r.GetString(20),PaymentSubmittedAt=r.IsDBNull(21)?null:r.GetString(21),TransferReference=r.GetString(22),
            IncludedHours=r.GetInt32(26),Players=r.GetInt32(25),SportTypeName=r.GetString(24), Equipment=System.Text.Json.JsonSerializer.Deserialize<List<BookingEquipment>>(r.GetString(23)) ?? [] });
        return result;
    }
}
