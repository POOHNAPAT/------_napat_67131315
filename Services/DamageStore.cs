using System.Data.Common;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private static void InitializeDamageReports(DbConnection c)
    {
        // Supabase upgrades are included in the embedded schema transaction.
        if(c is Npgsql.NpgsqlConnection)return;
        using var tx=BeginWrite(c);
        using var info=Cmd(c,"PRAGMA table_info(DamageReports)",tx);
        using var r=info.ExecuteReader();var exists=false;var hasOwner=false;
        while(r.Read()){exists=true;hasOwner |= r.GetString(1)=="UserId";}
        r.Close();
        string Schema(string table)=>$"""
            CREATE TABLE {table} (
              Id INTEGER PRIMARY KEY,UserId BIGINT NOT NULL REFERENCES Users(Id),BookingId BIGINT REFERENCES Bookings(Id),
              Subject TEXT NOT NULL DEFAULT '',Category TEXT NOT NULL CHECK(Category IN ('Court','Equipment','Other')),
              EquipmentId TEXT,EquipmentName TEXT NOT NULL DEFAULT '',Description TEXT NOT NULL,
              Status TEXT NOT NULL DEFAULT 'Reported' CHECK(Status IN ('Reported','InProgress','Resolved')),
              CreatedAt TEXT NOT NULL,UpdatedAt TEXT,UpdatedBy BIGINT REFERENCES Users(Id),AdminNote TEXT NOT NULL DEFAULT '');
            """;
        if(exists&&!hasOwner)
        {
            // SQLite cannot relax NOT NULL in place. Copy every field atomically; failures keep the old table.
            using var create=Cmd(c,Schema("DamageReports_upgrade"),tx);create.ExecuteNonQuery();
            using var copy=Cmd(c,"""
                INSERT INTO DamageReports_upgrade(Id,UserId,BookingId,Subject,Category,EquipmentId,EquipmentName,Description,Status,CreatedAt,UpdatedAt,UpdatedBy,AdminNote)
                SELECT d.Id,(SELECT UserId FROM Bookings WHERE Id=d.BookingId),d.BookingId,
                  COALESCE((SELECT CourtName FROM Bookings WHERE Id=d.BookingId),''),d.Category,d.EquipmentId,d.EquipmentName,
                  d.Description,d.Status,d.CreatedAt,d.UpdatedAt,d.UpdatedBy,d.AdminNote FROM DamageReports d;
                DROP TABLE DamageReports;
                ALTER TABLE DamageReports_upgrade RENAME TO DamageReports;
                """,tx);copy.ExecuteNonQuery();
        }
        else if(!exists){using var create=Cmd(c,Schema("DamageReports"),tx);create.ExecuteNonQuery();}
        using var indexes=Cmd(c,"CREATE INDEX IF NOT EXISTS IX_DamageReports_Booking ON DamageReports(BookingId); CREATE INDEX IF NOT EXISTS IX_DamageReports_User ON DamageReports(UserId)",tx);
        indexes.ExecuteNonQuery();tx.Commit();
    }

    public void ReportDamage(long userId,long? bookingId,DamageReportForm form)
    {
        if(form.Category is not ("Court" or "Equipment" or "Other") || string.IsNullOrWhiteSpace(form.Description)
            || form.Description.Trim().Length is <5 or >2000 || form.Subject?.Length>200)
            throw new BookingRuleException("เลือกประเภทปัญหา ระบุหัวข้อไม่เกิน 200 ตัวอักษร และรายละเอียด 5–2,000 ตัวอักษร");
        using var c=Open();using var tx=BeginWrite(c);
        RequireMember(c,userId,tx);
        BookingEquipment? item=null;string? courtName=null;
        if(bookingId.HasValue)
        {
            using var find=Cmd(c,"SELECT Status,EquipmentJson,CourtName FROM Bookings WHERE Id=$id AND UserId=$user",tx,("$id",bookingId),("$user",userId));
            using var r=find.ExecuteReader();
            if(!r.Read())throw new BookingRuleException("ไม่พบรายการจองของคุณ");
            if(r.GetString(0)=="Cancelled")throw new BookingRuleException("หากไม่ต้องการผูกกับรายการจองที่ยกเลิก กรุณาใช้หน้าแจ้งปัญหาทั่วไป");
            var equipment=System.Text.Json.JsonSerializer.Deserialize<List<BookingEquipment>>(r.GetString(1))??[];
            courtName=r.GetString(2);r.Close();
            item=form.Category=="Equipment"?equipment.SingleOrDefault(e=>e.Id==form.EquipmentId):null;
            if(form.Category=="Equipment"&&item is null)throw new BookingRuleException("เลือกอุปกรณ์ที่เช่าในรายการจองนี้ หรือใช้หน้าแจ้งปัญหาทั่วไป");
        }
        var subject=string.IsNullOrWhiteSpace(form.Subject)?courtName??(form.Category=="Court"?"ไม่ระบุสนาม":"ปัญหาการใช้งาน"):form.Subject.Trim();
        using var insert=Cmd(c,"""
            INSERT INTO DamageReports(UserId,BookingId,Subject,Category,EquipmentId,EquipmentName,Description,CreatedAt)
            VALUES($user,$booking,$subject,$category,$equipment,$name,$description,$now)
            """,tx,("$user",userId),("$booking",bookingId),("$subject",subject),("$category",form.Category),("$equipment",item?.Id??""),
            ("$name",item?.Name??(form.Category=="Equipment"?(string.IsNullOrWhiteSpace(form.Subject)?"ไม่ระบุอุปกรณ์":form.Subject.Trim()):"")),
            ("$description",form.Description.Trim()),("$now",DateTime.UtcNow.ToString("O")));
        insert.ExecuteNonQuery();tx.Commit();
    }

    public IReadOnlyList<DamageReport> DamageReports(long? userId=null,long? bookingId=null,string? status=null)
    {
        using var c=Open();
        using var cmd=Cmd(c,"""
            SELECT d.Id,d.BookingId,d.Subject,b.PlayDate,u.Name,u.Email,d.Category,d.EquipmentName,
              d.Description,d.Status,d.CreatedAt,d.UpdatedAt,d.AdminNote,COALESCE(a.Name,'')
            FROM DamageReports d JOIN Users u ON u.Id=d.UserId LEFT JOIN Bookings b ON b.Id=d.BookingId
            LEFT JOIN Users a ON a.Id=d.UpdatedBy
            WHERE ($user IS NULL OR d.UserId=$user) AND ($booking IS NULL OR d.BookingId=$booking)
              AND ($status='' OR d.Status=$status)
            ORDER BY CASE d.Status WHEN 'Reported' THEN 0 WHEN 'InProgress' THEN 1 ELSE 2 END,d.Id DESC
            """,null,("$user",userId),("$booking",bookingId),("$status",status??""));
        using var r=cmd.ExecuteReader();var result=new List<DamageReport>();
        while(r.Read())result.Add(new(r.GetInt64(0),r.IsDBNull(1)?null:r.GetInt64(1),r.GetString(2),r.IsDBNull(3)?null:Date(r.GetString(3)),r.GetString(4),r.GetString(5),
            r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.GetString(12),r.GetString(13)));
        return result;
    }

    public bool UpdateDamageReport(long id,long adminId,DamageReportUpdate form)
    {
        if(!DamageReport.Statuses.Contains(form.Status)||form.AdminNote?.Length>1000)
            throw new BookingRuleException("เลือกสถานะที่ถูกต้อง และระบุหมายเหตุไม่เกิน 1,000 ตัวอักษร");
        using var c=Open();using var cmd=Cmd(c,"""
            UPDATE DamageReports SET Status=$status,AdminNote=$note,UpdatedAt=$now,UpdatedBy=$admin
            WHERE Id=$id AND EXISTS(SELECT 1 FROM Users WHERE Id=$admin AND Role='Admin' AND IsLocked=0)
            """,null,("$id",id),("$admin",adminId),("$status",form.Status),("$note",form.AdminNote?.Trim()??""),("$now",DateTime.UtcNow.ToString("O")));
        return cmd.ExecuteNonQuery()==1;
    }
}
