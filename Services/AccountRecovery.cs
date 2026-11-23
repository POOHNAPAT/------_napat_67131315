using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourtBook.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace CourtBook.Services;

public partial class BookingStore
{
    public SiteUser? FindUserByEmail(string email)
    {
        using var c=Open();using var cmd=Cmd(c,"SELECT Id,Name,Email,PasswordHash,Role FROM Users WHERE lower(Email)=lower($email) AND IsLocked=0",null,("$email",email.Trim()));
        using var r=cmd.ExecuteReader();return r.Read()?ReadUser(r):null;
    }
    public bool UpdateProfile(long userId,ProfileForm form)
    {
        var user=FindUser(userId);
        if(user is null) return false;
        if(!user.Email.Equals(form.Email.Trim(),StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(form.CurrentPassword) || hasher.VerifyHashedPassword(user,user.PasswordHash,form.CurrentPassword)==PasswordVerificationResult.Failed))
            throw new BookingRuleException("การเปลี่ยนอีเมลต้องยืนยันรหัสผ่านปัจจุบัน");
        using var c=Open();using var cmd=Cmd(c,"UPDATE Users SET Name=$name,Email=$email WHERE Id=$id AND IsLocked=0",null,("$id",userId),("$name",form.Name.Trim()),("$email",form.Email.Trim()));
        try{return cmd.ExecuteNonQuery()==1;}
        catch(System.Data.Common.DbException ex) when(IsUniqueViolation(ex)){throw new BookingRuleException("อีเมลนี้มีบัญชีอยู่แล้ว");}
    }
    public bool ResetPassword(SiteUser user,string newPassword)
    {
        using var c=Open();using var cmd=Cmd(c,"UPDATE Users SET PasswordHash=$hash WHERE Id=$id AND PasswordHash=$old AND IsLocked=0",null,
            ("$hash",hasher.HashPassword(user,newPassword)),("$id",user.Id),("$old",user.PasswordHash));
        return cmd.ExecuteNonQuery()==1;
    }
}

public class AccountRecovery(BookingStore store,IDataProtectionProvider protection,IConfiguration configuration,IWebHostEnvironment environment)
{
    private readonly ITimeLimitedDataProtector protector=protection.CreateProtector("CourtBook.PasswordReset.v1").ToTimeLimitedDataProtector();
    private bool Pickup => configuration["Email:Delivery"]=="Pickup" && (environment.IsDevelopment()||environment.IsEnvironment("Testing"));
    public bool Configured => Pickup || (!string.IsNullOrWhiteSpace(configuration["Email:Host"]) && !string.IsNullOrWhiteSpace(configuration["Email:From"]));
    public static string Version(SiteUser user)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash+"|"+user.Email)));
    public SiteUser? Validate(string token,bool admin)
    {
        if(string.IsNullOrEmpty(token)||token.Length>4096)return null;
        try
        {
            var data=JsonSerializer.Deserialize<ResetPayload>(protector.Unprotect(token));
            if(data is null)return null;
            var user=store.FindUser(data.Id);
            return user is not null && (user.Role=="Admin")==admin && data.Version==Version(user)?user:null;
        }
        catch(Exception ex) when(ex is CryptographicException or JsonException or FormatException){return null;}
    }
    public async Task Send(string email,bool admin)
    {
        var user=store.FindUserByEmail(email);
        if(user is null || (user.Role=="Admin")!=admin)return;
        var origin=configuration[admin?"Account:AdminOrigin":"Account:PublicOrigin"];
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var baseUri) || (baseUri.Scheme!="https" && !(baseUri.Scheme=="http" && baseUri.IsLoopback)))
            throw new InvalidOperationException("Configure Account public origin with HTTPS.");
        var token=protector.Protect(JsonSerializer.Serialize(new ResetPayload(user.Id,Version(user))),TimeSpan.FromMinutes(30));
        var link=new Uri(baseUri,"/Account/ResetPassword?token="+Uri.EscapeDataString(token));
        using var message=new MailMessage(configuration["Email:From"]??"courtbook@localhost",user.Email,"COURTBOOK – ตั้งรหัสผ่านใหม่",
            $"คุณขอตั้งรหัสผ่านใหม่สำหรับ COURTBOOK\nเปิดลิงก์นี้ภายใน 30 นาที ใช้ได้ครั้งเดียว:\n{link}\nหากไม่ได้ขอ กรุณาเพิกเฉยต่ออีเมลนี้");
        using var smtp=new SmtpClient();
        if(Pickup)
        {
            var directory=Path.GetFullPath(configuration["Email:PickupDirectory"]??"App_Data/mail",environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            smtp.DeliveryMethod=SmtpDeliveryMethod.SpecifiedPickupDirectory;smtp.PickupDirectoryLocation=directory;
        }
        else
        {
            smtp.Host=configuration["Email:Host"]!;smtp.Port=configuration.GetValue("Email:Port",587);smtp.EnableSsl=true;
            smtp.Credentials=new NetworkCredential(configuration["Email:Username"],configuration["Email:Password"]);
        }
        await smtp.SendMailAsync(message);
    }
    private record ResetPayload(long Id,string Version);
}
