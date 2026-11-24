using System.Security.Cryptography;
using CourtBook.Models;
using System.Data.Common;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeWallet(DbConnection c)
    {
        using var cmd = Cmd(c, """
            CREATE TABLE IF NOT EXISTS TopUps (
              Id INTEGER PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES Users(Id),
              AmountSatang INTEGER NOT NULL CHECK(AmountSatang BETWEEN 1 AND 10000000),
              TransferReference TEXT COLLATE NOCASE NOT NULL UNIQUE, SlipData BLOB NOT NULL,
              SlipContentType TEXT NOT NULL, SlipHash TEXT NOT NULL UNIQUE,
              Status TEXT NOT NULL DEFAULT 'Pending' CHECK(Status IN ('Pending','Approved','Rejected')),
              CreatedAt TEXT NOT NULL, ReviewedAt TEXT, ReviewedBy INTEGER REFERENCES Users(Id), ReviewNote TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS MembershipPlans (
              Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Months INTEGER NOT NULL CHECK(Months IN (1,12)),
              PriceSatang INTEGER NOT NULL CHECK(PriceSatang>=0), DiscountPercent INTEGER NOT NULL CHECK(DiscountPercent BETWEEN 0 AND 100),
              IsActive INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO MembershipPlans(Id,Name,Months,PriceSatang,DiscountPercent,IsActive) VALUES('monthly','แพ็กรายเดือน',1,0,0,0),('yearly','แพ็กรายปี',12,0,0,0);
            CREATE TABLE IF NOT EXISTS Memberships (
              Id INTEGER PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES Users(Id), PlanId TEXT NOT NULL REFERENCES MembershipPlans(Id),
              PlanName TEXT NOT NULL, PriceSatang INTEGER NOT NULL, DiscountPercent INTEGER NOT NULL,
              Starts TEXT NOT NULL, Ends TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WalletEntries (
              Id INTEGER PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES Users(Id), AmountSatang INTEGER NOT NULL CHECK(AmountSatang<>0),
              Description TEXT NOT NULL, CreatedAt TEXT NOT NULL,
              TopUpId INTEGER UNIQUE REFERENCES TopUps(Id), BookingId INTEGER UNIQUE REFERENCES Bookings(Id),
              MembershipId INTEGER UNIQUE REFERENCES Memberships(Id));
            CREATE INDEX IF NOT EXISTS IX_WalletEntries_User ON WalletEntries(UserId);
            CREATE INDEX IF NOT EXISTS IX_Memberships_User ON Memberships(UserId);
            CREATE TABLE IF NOT EXISTS DemoPayments (
              Id TEXT PRIMARY KEY, UserId INTEGER NOT NULL REFERENCES Users(Id),
              AmountSatang INTEGER NOT NULL CHECK(AmountSatang BETWEEN 100 AND 10000000),
              Status TEXT NOT NULL DEFAULT 'Pending' CHECK(Status IN ('Pending','Paid','Failed','Cancelled')),
              CreatedAt TEXT NOT NULL, CompletedAt TEXT);
            """);
        cmd.ExecuteNonQuery();
    }

    private static long Balance(DbConnection c, long userId, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, "SELECT COALESCE(SUM(AmountSatang),0) FROM WalletEntries WHERE UserId=$user", tx, ("$user", userId));
        return (long)cmd.ExecuteScalar()!;
    }

    public long WalletBalance(long userId) { using var c = Open(); return Balance(c, userId); }

    public string StartDemoPayment(long userId, decimal amount)
    {
        if (amount < 1 || amount > 100000 || decimal.Round(amount, 2) != amount)
            throw new BookingRuleException("ระบุยอดเครดิตทดลอง 1–100,000 บาท ทศนิยมไม่เกิน 2 ตำแหน่ง");
        using var c = Open();
        RequireMember(c, userId);
        var id = "DEMO-" + Guid.NewGuid().ToString("N");
        using var cmd = Cmd(c, """
            INSERT INTO DemoPayments(Id,UserId,AmountSatang,CreatedAt) VALUES($id,$user,$amount,$now)
            """, null, ("$id", id), ("$user", userId), ("$amount", Satang(amount)), ("$now", DateTime.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
        return id;
    }

    public DemoPayment? FindDemoPayment(string id, long userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,AmountSatang,Status,CreatedAt FROM DemoPayments WHERE Id=$id AND UserId=$user",
            null, ("$id", id), ("$user", userId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3)) : null;
    }

    public bool CompleteDemoPayment(string id, long userId, string status)
    {
        if (status is not ("Paid" or "Failed" or "Cancelled"))
            throw new BookingRuleException("ผลการชำระเงินทดลองไม่ถูกต้อง");
        using var c = Open();
        using var tx = BeginWrite(c);
        RequireMember(c, userId, tx);
        var now = DateTime.UtcNow;
        // One atomic transition owns the credit, including concurrent retries and cancellations.
        using var update = Cmd(c, """
            UPDATE DemoPayments SET Status=$status,CompletedAt=$now
            WHERE Id=$id AND UserId=$user AND Status='Pending' AND CreatedAt>=$cutoff
            RETURNING AmountSatang
            """, tx, ("$id", id), ("$user", userId), ("$status", status),
            ("$now", now.ToString("O")), ("$cutoff", now.AddMinutes(-15).ToString("O")));
        var amount = update.ExecuteScalar();
        if (amount is null) return false;
        if (status == "Paid")
        {
            using var credit = Cmd(c, """
                INSERT INTO WalletEntries(UserId,AmountSatang,Description,CreatedAt)
                VALUES($user,$amount,$description,$now)
                """, tx, ("$user", userId), ("$amount", (long)amount),
                ("$description", $"เครดิตทดลองอัตโนมัติ {id} (ไม่ใช่เงินจริง)"), ("$now", now.ToString("O")));
            credit.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    private static void RequireMember(DbConnection c, long userId, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM Users WHERE Id=$user AND Role='Member'", tx, ("$user", userId));
        if ((long)cmd.ExecuteScalar()! != 1) throw new BookingRuleException("ไม่พบบัญชีสมาชิก");
    }

    public IReadOnlyList<MembershipPlan> Plans()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,Name,Months,PriceSatang,DiscountPercent,IsActive,MonthlyHours,QuotaSportTypeId,QuotaSportName FROM MembershipPlans WHERE Id IN ('monthly','yearly') ORDER BY Months");
        using var r = cmd.ExecuteReader();
        var result = new List<MembershipPlan>();
        while (r.Read()) result.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt64(3),r.GetInt32(4),(Convert.ToInt32(r.GetValue(5)) != 0),r.GetInt32(6),r.GetInt64(7),r.GetString(8)));
        return result;
    }

    public bool SavePlan(PlanForm form)
    {
        if (form.Id is not ("monthly" or "yearly") || (form.Months.HasValue && form.Months != (form.Id=="monthly"?1:12)) || form.Price < 1 || form.Price > 1000000 || form.DiscountPercent is < 1 or > 100)
            throw new BookingRuleException("ราคาแพ็กหรือส่วนลดไม่ถูกต้อง");
        if(form.MonthlyHours is <0 or >100)throw new BookingRuleException("กำหนดโควตา 0–100 ชั่วโมงต่อเดือน");
        using var c = Open();
        var sportName="";
        if(form.MonthlyHours>0){using var sport=Cmd(c,"SELECT Name FROM SportTypes WHERE Id=$id",null,("$id",form.QuotaSportTypeId));sportName=sport.ExecuteScalar() as string??throw new BookingRuleException("เลือกประเภทกีฬาที่ใช้สิทธิ์แพ็ก");}
        using var cmd = Cmd(c, "UPDATE MembershipPlans SET Months=CASE WHEN $months=0 THEN Months ELSE $months END,PriceSatang=$price,DiscountPercent=$discount,IsActive=$active,MonthlyHours=$quota,QuotaSportTypeId=$sport,QuotaSportName=$sportName WHERE Id=$id", null,
            ("$id", form.Id),("$months",form.Months??0),("$price",Satang(form.Price)),("$discount",form.DiscountPercent),("$active",form.IsActive),("$quota",form.MonthlyHours),("$sport",form.MonthlyHours>0?form.QuotaSportTypeId:0),("$sportName",sportName));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static Membership? ActiveMembership(DbConnection c, long userId, DbTransaction? tx = null)
    {
        using var cmd = Cmd(c, """
            SELECT Id,PlanId,PlanName,Starts,Ends,DiscountPercent,MonthlyHours,QuotaSportTypeId,QuotaSportName FROM Memberships
            WHERE UserId=$user AND Starts<=$today AND Ends>$today ORDER BY Id DESC LIMIT 1
            """, tx,("$user",userId),("$today",CourtTime.Iso(CourtTime.Today)));
        using var r = cmd.ExecuteReader();
        if(!r.Read())return null;
        var member=new Membership(r.GetInt64(0),r.GetString(1),r.GetString(2),Date(r.GetString(3)),Date(r.GetString(4)),r.GetInt32(5),r.GetInt32(6),r.GetInt64(7),r.GetString(8));r.Close();
        return member with {Quotas=QuotaBalances(c,member,tx)};
    }

    public WalletPage Wallet(long userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT Id,AmountSatang,Description,CreatedAt FROM WalletEntries WHERE UserId=$user ORDER BY Id DESC",null,("$user",userId));
        using var r = cmd.ExecuteReader();
        var entries = new List<WalletEntry>();
        while(r.Read()) entries.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3)));
        r.Close();
        return new(Balance(c,userId),entries,TopUps(userId),Plans(),ActiveMembership(c,userId));
    }

    public long SubmitTopUp(long userId, TopUpForm form)
    {
        if (form.Amount < 0.01m || form.Amount > 100000 || decimal.Round(form.Amount,2) != form.Amount)
            throw new BookingRuleException("ระบุยอดเติมเงิน 0.01–100,000 บาท ทศนิยมไม่เกิน 2 ตำแหน่ง");
        if(form.CheckoutPath!=""&&!WalletCheckout.SafePath(form.CheckoutPath))throw new BookingRuleException("รายการสำหรับกลับไปชำระไม่ถูกต้อง");
        if (form.TransferReference?.Trim().Length > 200)
            throw new BookingRuleException("เลขรายการโอนต้องไม่เกิน 200 ตัวอักษร");
        var image = ReadUploadedImage(form.Slip) ?? throw new BookingRuleException("กรุณาแนบสลิปโอนเงิน");
        var reference = string.IsNullOrWhiteSpace(form.TransferReference)
            ? "SLIP-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Data))
            : form.TransferReference.Trim();
        using var c = Open();
        using var tx = BeginWrite(c);
        RequireMember(c,userId,tx);
        using var used = Cmd(c,"SELECT COUNT(*) FROM Bookings WHERE lower(TransferReference)=lower($reference) OR SlipData=$image",tx,("$reference",reference),("$image",image.Data));
        if ((long)used.ExecuteScalar()! > 0) throw new BookingRuleException("เลขโอนนี้เคยใช้ชำระค่าจองแล้ว");
        using var cmd = Cmd(c,"""
            INSERT INTO TopUps(UserId,AmountSatang,TransferReference,SlipData,SlipContentType,SlipHash,CreatedAt,CheckoutPath)
            VALUES($user,$amount,$reference,$image,$mime,$hash,$now,$path) RETURNING Id
            """,tx,("$user",userId),("$amount",Satang(form.Amount)),("$reference",reference),
            ("$image",image.Data),("$mime",image.ContentType),("$hash",Convert.ToHexString(SHA256.HashData(image.Data))),("$now",DateTime.UtcNow.ToString("O")),("$path",form.CheckoutPath));
        try { var id=(long)cmd.ExecuteScalar()!; tx.Commit(); return id; }
        catch(DbException ex) when(IsUniqueViolation(ex)) { throw new BookingRuleException("เลขรายการโอนหรือสลิปนี้ถูกส่งแล้ว กรุณาตรวจประวัติหรือติดต่อผู้ดูแล"); }
    }

    public IReadOnlyList<TopUp> TopUps(long? userId = null)
    {
        using var c = Open();
        using var cmd = Cmd(c,"""
            SELECT t.Id,t.UserId,u.Name,t.AmountSatang,t.TransferReference,t.Status,t.CreatedAt,t.ReviewNote,t.CheckoutPath
            FROM TopUps t JOIN Users u ON u.Id=t.UserId WHERE $user IS NULL OR t.UserId=$user ORDER BY t.Id DESC
            """,null,("$user",userId));
        using var r = cmd.ExecuteReader();
        var result = new List<TopUp>();
        while(r.Read()) result.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8)));
        return result;
    }

    public CourtImage? TopUpSlip(long id, long? userId = null)
    {
        using var c = Open();
        using var cmd = Cmd(c,"SELECT SlipData,SlipContentType FROM TopUps WHERE Id=$id AND ($user IS NULL OR UserId=$user)",null,("$id",id),("$user",userId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new((byte[])r[0],r.GetString(1)) : null;
    }

    public bool ReviewTopUp(long id, long adminId, bool approve, string? note)
    {
        if (note?.Length > 200 || (!approve && string.IsNullOrWhiteSpace(note)))
            throw new BookingRuleException("กรุณาระบุเหตุผลไม่อนุมัติ ความยาวไม่เกิน 200 ตัวอักษร");
        using var c = Open();
        using var tx = BeginWrite(c);
        using var cmd = Cmd(c,"""
            UPDATE TopUps SET Status=$status,ReviewedAt=$now,ReviewedBy=$admin,ReviewNote=$note
            WHERE Id=$id AND Status='Pending' AND EXISTS(SELECT 1 FROM Users WHERE Id=$admin AND Role='Admin')
            RETURNING UserId,AmountSatang
            """,tx,("$id",id),("$admin",adminId),("$status",approve?"Approved":"Rejected"),("$note",note?.Trim()??""),("$now",DateTime.UtcNow.ToString("O")));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        var userId=r.GetInt64(0); var amount=r.GetInt64(1); r.Close();
        if(approve)
        {
            using var credit=Cmd(c,"INSERT INTO WalletEntries(UserId,AmountSatang,Description,CreatedAt,TopUpId) VALUES($user,$amount,$description,$now,$id)",tx,
                ("$user",userId),("$amount",amount),("$description",$"เติมเงิน TU{id:000000}"),("$now",DateTime.UtcNow.ToString("O")),("$id",id));
            credit.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    }

    private static void Debit(DbConnection c, DbTransaction tx, long userId, long amount, string description, long? bookingId=null, long? membershipId=null)
    {
        if(amount <= 0 || Balance(c,userId,tx)<amount) throw new BookingRuleException("ยอดเงินในกระเป๋าไม่เพียงพอ กรุณาเติมเงินก่อน");
        using var cmd=Cmd(c,"INSERT INTO WalletEntries(UserId,AmountSatang,Description,CreatedAt,BookingId,MembershipId) VALUES($user,$amount,$description,$now,$booking,$membership)",tx,
            ("$user",userId),("$amount",-amount),("$description",description),("$now",DateTime.UtcNow.ToString("O")),("$booking",bookingId),("$membership",membershipId));
        cmd.ExecuteNonQuery();
    }

    public long BuyPlan(long userId, string planId, long expectedPriceSatang, int expectedDiscountPercent, int? expectedMonths = null,int expectedMonthlyHours=0,long expectedQuotaSportTypeId=0)
    {
        if(planId is not ("monthly" or "yearly")) throw new BookingRuleException("แพ็กนี้ไม่มีให้สมัครแล้ว กรุณาเลือกแพ็กรายเดือนหรือรายปี");
        using var c=Open(); using var tx=BeginWrite(c);
        RequireMember(c,userId,tx);
        if(ActiveMembership(c,userId,tx) is not null) throw new BookingRuleException("คุณมีแพ็กที่ใช้งานอยู่แล้ว ซื้อแพ็กใหม่ได้เมื่อแพ็กเดิมหมดอายุ");
        using var find=Cmd(c,"SELECT Name,Months,PriceSatang,DiscountPercent,MonthlyHours,QuotaSportTypeId,QuotaSportName FROM MembershipPlans WHERE Id=$id AND IsActive=1 AND PriceSatang>0 AND DiscountPercent>0",tx,("$id",planId));
        using var r=find.ExecuteReader();
        if(!r.Read()) throw new BookingRuleException("แพ็กนี้ยังไม่เปิดขาย");
        var name=r.GetString(0); var months=r.GetInt32(1); var price=r.GetInt64(2); var discount=r.GetInt32(3);var quota=r.GetInt32(4);var sport=r.GetInt64(5);var sportName=r.GetString(6);r.Close();
        if(quota!=expectedMonthlyHours || sport!=expectedQuotaSportTypeId || price!=expectedPriceSatang || discount!=expectedDiscountPercent || months!=(expectedMonths??(planId=="monthly"?1:planId=="yearly"?12:0))) throw new BookingRuleException("ราคาแพ็กหรือสิทธิ์เปลี่ยนแล้ว กรุณาตรวจรายละเอียดและซื้ออีกครั้ง");
        var starts=CourtTime.Today; var ends=starts.AddMonths(months);
        using var insert=Cmd(c,"""
            INSERT INTO Memberships(UserId,PlanId,PlanName,PriceSatang,DiscountPercent,Starts,Ends,CreatedAt,MonthlyHours,QuotaSportTypeId,QuotaSportName)
            VALUES($user,$plan,$name,$price,$discount,$starts,$ends,$now,$quota,$sport,$sportName) RETURNING Id
            """,tx,("$user",userId),("$plan",planId),("$name",name),("$price",price),("$discount",discount),
            ("$starts",CourtTime.Iso(starts)),("$ends",CourtTime.Iso(ends)),("$now",DateTime.UtcNow.ToString("O")),("$quota",quota),("$sport",sport),("$sportName",sportName));
        var id=(long)insert.ExecuteScalar()!;
        Debit(c,tx,userId,price,$"ซื้อ{name}",membershipId:id);
        IssueMembershipReceipt(c,tx,id);
        tx.Commit();return id;
    }

    public bool PayBookingWithWallet(long id, long userId)
    {
        using var c=Open(); using var tx=BeginWrite(c);
        RequireMember(c,userId,tx);
        using var cmd=Cmd(c,"""
            UPDATE Bookings SET PaidAt=$now,PaymentReference=$reference
            WHERE Id=$id AND UserId=$user AND Status IN ('Pending','Confirmed')
            AND PaidAt IS NULL AND PaymentSubmittedAt IS NULL AND NetSatang>0 RETURNING NetSatang,PlayDate,StartHour,Hours
            """,tx,("$id",id),("$user",userId),("$now",DateTime.UtcNow.ToString("O")),("$reference",$"กระเป๋าเงิน CB{id:000000}"));
        using var r=cmd.ExecuteReader();
        if(!r.Read())return false;
        var amount=r.GetInt64(0);
        var end=Date(r.GetString(1)).ToDateTime(new TimeOnly(r.GetInt32(2),0)).AddHours(r.GetInt32(3));
        r.Close();
        if(end<=CourtTime.Now)throw new BookingRuleException("พ้นช่วงเวลาจองแล้ว ไม่สามารถชำระค่าจองรายการนี้ กรุณาติดต่อผู้ดูแล");
        Debit(c,tx,userId,amount,$"ชำระค่าจอง CB{id:000000}",bookingId:id);
        IssueBookingReceipt(c,tx,id);
        tx.Commit(); return true;
    }
}
