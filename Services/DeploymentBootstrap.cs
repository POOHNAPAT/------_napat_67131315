using System.ComponentModel.DataAnnotations;
using CourtBook.Models;

namespace CourtBook.Services;

public partial class BookingStore
{
    private void BootstrapAdmin(System.Data.Common.DbConnection c,IConfiguration configuration)
    {
        var email=configuration["Bootstrap:AdminEmail"];
        var password=configuration["Bootstrap:AdminPassword"];
        if(string.IsNullOrWhiteSpace(email)&&string.IsNullOrWhiteSpace(password))return;
        using var tx=BeginWrite(c);
        using var count=Cmd(c,"SELECT COUNT(*) FROM Users WHERE Role='Admin'",tx);
        if((long)count.ExecuteScalar()!>0){tx.Commit();return;}
        if(string.IsNullOrWhiteSpace(email)||!new EmailAddressAttribute().IsValid(email.Trim())||email.Trim().Length>150||password is null||password.Length is <12 or >128)
            throw new InvalidOperationException("Set Bootstrap:AdminEmail and Bootstrap:AdminPassword (12–128 characters) to create the first administrator.");
        using var existing=Cmd(c,"SELECT COUNT(*) FROM Users WHERE lower(Email)=lower($email)",tx,("$email",email.Trim()));
        if((long)existing.ExecuteScalar()!>0)throw new InvalidOperationException("Bootstrap email already belongs to an account. Use a distinct email for the first administrator.");
        InsertUser(c,tx,"ผู้ดูแล COURTBOOK",email.Trim(),password,"Admin");
        tx.Commit();
    }
}
