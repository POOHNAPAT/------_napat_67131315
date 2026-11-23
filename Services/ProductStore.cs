using System.Data.Common;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeProductFeatures(DbConnection c)
    {
        using var tx=BeginWrite(c);
        void Column(string table,string name,string definition)
        {
            if(c is Microsoft.Data.Sqlite.SqliteConnection)
            {
                using var info=Cmd(c,$"PRAGMA table_info({table})",tx);
                using var r=info.ExecuteReader();
                var exists=false;
                while(r.Read()) exists |= r.GetString(1)==name;
                r.Close();
                if(exists) return;
            }
            using var alter=Cmd(c,$"ALTER TABLE {table} ADD COLUMN {(c is Npgsql.NpgsqlConnection ? "IF NOT EXISTS " : "")}{name} {definition}",tx);
            alter.ExecuteNonQuery();
        }
        Column("Promotions","MinPlayers","INTEGER NOT NULL DEFAULT 4");
        Column("Courts","Location","TEXT NOT NULL DEFAULT ''");
        Column("Bookings","Players","INTEGER NOT NULL DEFAULT 1");
        Column("Bookings","SportTypeName","TEXT NOT NULL DEFAULT ''");
        Column("RentalEquipment","ImageUrl","TEXT NOT NULL DEFAULT ''");
        Column("Users","IsLocked","INTEGER NOT NULL DEFAULT 0");
        using var schema=Cmd(c,"""
            CREATE TABLE IF NOT EXISTS CourtReviews (
              CourtId BIGINT NOT NULL REFERENCES Courts(Id),UserId BIGINT NOT NULL REFERENCES Users(Id),
              Rating INTEGER NOT NULL CHECK(Rating BETWEEN 1 AND 5),Comment TEXT NOT NULL,CreatedAt TEXT NOT NULL,
              PRIMARY KEY(CourtId,UserId));
            UPDATE MembershipPlans SET IsActive=0 WHERE Id IN ('basic','premium','vip');
            UPDATE MembershipPlans SET Months=CASE WHEN Id='monthly' THEN 1 ELSE 12 END WHERE Id IN ('monthly','yearly');
            """,tx);
        schema.ExecuteNonQuery();
        // Defaults are editable rental offers; never add fictitious court locations, ratings or revenue.
        using var sports=Cmd(c,"SELECT Id,Name FROM SportTypes",tx);
        using var reader=sports.ExecuteReader();
        var types=new List<(long Id,string Name)>();
        while(reader.Read()) types.Add((reader.GetInt64(0),reader.GetString(1)));
        reader.Close();
        foreach(var sport in types)
        {
            var name=sport.Name.ToLowerInvariant();
            var items=name.Contains("ฟุตบอล")||name.Contains("football")
                ? new[]{("football","ลูกฟุตบอล","ลูก",5000),("bib","เสื้อแบ่งทีม / ชุดฝึกซ้อม","ชุด",3000)}
                : name.Contains("เทนนิส")||name.Contains("tennis")
                ? new[]{("tennis-racket","ไม้เทนนิส","อัน",5000),("tennis-ball","ชุดลูกเทนนิส","ชุด",3000)}
                : name.Contains("แบด")||name.Contains("badminton")
                ? new[]{("shuttlecock","ชุดลูกแบดมินตัน","ชุด",3000)} : [];
            foreach(var (key,label,unit,price) in items)
            {
                using var insert=Cmd(c,"""
                    INSERT INTO RentalEquipment(Id,SportTypeId,Name,Unit,PriceSatang,IsActive,ImageUrl)
                    VALUES($id,$sport,$name,$unit,$price,1,$image) ON CONFLICT DO NOTHING
                    """,tx,("$id",$"{key}-{sport.Id}"),("$sport",sport.Id),("$name",label),("$unit",unit),("$price",price),
                    ("$image",$"/images/equipment/{key}.svg"));
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public IReadOnlyList<CourtReview> Reviews(long courtId)
    {
        using var c=Open();
        using var cmd=Cmd(c,"SELECT u.Name,r.Rating,r.Comment,r.CreatedAt FROM CourtReviews r JOIN Users u ON u.Id=r.UserId WHERE r.CourtId=$court ORDER BY r.CreatedAt DESC",null,("$court",courtId));
        using var r=cmd.ExecuteReader();
        var rows=new List<CourtReview>();
        while(r.Read()) rows.Add(new(r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetString(3)));
        return rows;
    }
    private static bool CanReview(DbConnection c,long userId,long courtId,DbTransaction? tx=null)
    {
        using var cmd=Cmd(c,"""
            SELECT COUNT(*) FROM Bookings WHERE UserId=$user AND CourtId=$court AND Status='Confirmed'
            AND (PlayDate<$today OR (PlayDate=$today AND StartHour+Hours<=$hour))
            """,tx,("$user",userId),("$court",courtId),("$today",CourtTime.Iso(CourtTime.Today)),("$hour",CourtTime.Now.Hour));
        return (long)cmd.ExecuteScalar()!>0;
    }
    public bool CanReview(long userId,long courtId){using var c=Open();return CanReview(c,userId,courtId);}
    public void Review(long userId,ReviewForm form)
    {
        if(form.Rating is <1 or >5 || form.Comment?.Length>500) throw new BookingRuleException("ให้คะแนน 1–5 และข้อความไม่เกิน 500 ตัวอักษร");
        using var c=Open();using var tx=BeginWrite(c);
        if(GetCourt(c,form.CourtId,tx) is null || !CanReview(c,userId,form.CourtId,tx)) throw new BookingRuleException("รีวิวได้หลังใช้สนามจากรายการจองที่ยืนยันแล้วเท่านั้น");
        using var cmd=Cmd(c,"""
            INSERT INTO CourtReviews(CourtId,UserId,Rating,Comment,CreatedAt) VALUES($court,$user,$rating,$comment,$now)
            ON CONFLICT(CourtId,UserId) DO UPDATE SET Rating=excluded.Rating,Comment=excluded.Comment,CreatedAt=excluded.CreatedAt
            """,tx,("$court",form.CourtId),("$user",userId),("$rating",form.Rating),("$comment",form.Comment?.Trim()??""),("$now",DateTime.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();tx.Commit();
    }
    public RevenuePage Revenue(DateOnly from,DateOnly to)
    {
        if(from>to || to.DayNumber-from.DayNumber>366) throw new BookingRuleException("เลือกช่วงวันที่ไม่เกิน 366 วัน และวันสิ้นสุดต้องไม่น้อยกว่าวันเริ่มต้น");
        var bookings=Bookings().Where(b=>b.Date>=from && b.Date<=to).ToList();
        var days=bookings.GroupBy(b=>b.Date).OrderBy(g=>g.Key).Select(g=>new RevenueDay(g.Key,g.Count(),g.Where(b=>b.PaidAt is not null).Sum(b=>b.NetSatang))).ToList();
        return new(from,to,bookings.Where(b=>b.PaidAt is not null).Sum(b=>b.NetSatang),bookings.Where(b=>b.PaidAt is null && b.ApprovalStatus!="Cancelled").Sum(b=>b.NetSatang),bookings.Count(b=>b.ApprovalStatus=="Cancelled"),days,bookings);
    }
    public bool SetCustomerLocked(long id,bool locked)
    {
        using var c=Open();using var cmd=Cmd(c,"UPDATE Users SET IsLocked=$locked WHERE Id=$id AND Role='Member'",null,("$id",id),("$locked",locked));
        return cmd.ExecuteNonQuery()==1;
    }
    public bool CustomerLocked(long id)
    {
        using var c=Open();using var cmd=Cmd(c,"SELECT IsLocked FROM Users WHERE Id=$id",null,("$id",id));
        return Convert.ToInt32(cmd.ExecuteScalar()??0)==1;
    }
}
