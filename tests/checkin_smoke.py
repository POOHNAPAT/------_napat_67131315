"""Private entry passes, explicit staff check-in, eligibility and concurrency on an isolated host."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import datetime as dt
import importlib.util
import json
import os
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check, fund_and_pay
from product_smoke import port


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-checkin-') as directory:
        database=Path(directory)/'test.db';base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(database),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Logging__LogLevel__Default':'Warning'}
        def sql(statement,args=()):
            with closing(sqlite3.connect(database)) as c:rows=c.execute(statement,args).fetchall();c.commit();return rows
        with (Path(directory)/'server.log').open('w',encoding='utf-8') as log:
            server=None
            def start():
                nonlocal server
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(150):
                    try:
                        if Browser(base).request('/')[0]==200:return
                    except OSError:pass
                    if server.poll() is not None:raise RuntimeError('Host exited')
                    time.sleep(.1)
                raise RuntimeError('Host not ready')
            def stop():
                if server and server.poll() is None:server.terminate();server.wait(timeout=15)
            try:
                start();member=Browser(base);other=Browser(base);admin=Browser(admin_base)
                for browser,email in [(member,'entry@example.com'),(other,'other-entry@example.com')]:
                    browser.post('/Account/Register',{'Name':'Entry Player','Email':email,'Password':PASSWORD,'ConfirmPassword':PASSWORD});browser.login(email)
                admin.login('admin@courtbook.local','CourtBook123!')
                date=TODAY+dt.timedelta(days=4)
                def book(hour):
                    response=member.post('/Bookings/Create',{'CourtId':1,'Date':str(date),'Hours':[hour]});return int(response[1].rsplit('/',1)[1])
                booking=book(10);page=f'/Bookings/EntryPass/{booking}';admin_page='/Admin/CheckIn'
                def number(id):return f'CB{id:06d}'
                def confirm(code,identity=True):return admin.post('/Admin/ConfirmCheckIn',{'Code':code,'IdentityVerified':str(identity).lower()},token_page=admin_page)
                check(admin.request(admin_page)[0]==200 and member.request(admin_page)[0]==404,'Check-in scanner and confirmation are restricted to admin portal')
                check('id="entry-qr"' not in member.request(page)[2] and member.request(f'/Bookings/EntryQr/{booking}')[0]==404,'Pending unpaid booking cannot show an admission QR')
                fund_and_pay(member,booking)
                check('id="entry-qr"' not in member.request(page)[2],'Payment alone cannot bypass booking approval for a QR')
                admin.post(f'/Admin/ConfirmBooking/{booking}',{},token_page=f'/Admin/Booking/{booking}')
                check('id="entry-qr"' in member.request(page)[2],'Confirmed and paid customer can display the entry pass')
                payload='COURTBOOK:'+sql('SELECT Token FROM BookingEntryPasses WHERE BookingId=?',(booking,))[0][0]
                check(len(payload)==53 and number(booking) not in payload,'QR uses an opaque random token rather than a guessable booking id')
                with member.opener.open(base+f'/Bookings/EntryQr/{booking}') as response:
                    data=response.read();check(data.startswith(bytes([137,80,78,71])) and len(data)>100 and response.headers['Cache-Control']=='no-store','QR is a generated private PNG with cache disabled')
                check(other.request(page+'?userId=1')[0]==404 and other.request(f'/Bookings/EntryQr/{booking}')[0]==404,'Another customer cannot obtain a pass or QR by forging ownership')
                check('EntryPass' in member.request('/Bookings/History')[2] and 'EntryPass' in member.request(f'/Receipts/Details/RC-BK-{booking:06d}')[2],'Customer history and booking receipt link to the pass')
                lookup=admin.post('/Admin/CheckInLookup',{'Code':payload},token_page=admin_page)
                check(number(booking) in lookup[2] and sql('SELECT COUNT(*) FROM BookingCheckIns')[0][0]==0,'Scanning only previews details and does not check a customer in automatically')
                for code in ['COURTBOOK:'+'A'*43,'https://example.com','CB999999999','', 'x'*129]:
                    check(admin.post('/Admin/CheckInLookup',{'Code':code},token_page=admin_page)[0]==200,'Unknown, malformed or empty codes fail safely')
                check(admin.request('/Admin/ConfirmCheckIn',{'Code':payload,'IdentityVerified':'true'})[0]==400,'Check-in confirmation requires CSRF')
                confirm(payload);check(not sql('SELECT * FROM BookingCheckIns'),'A valid QR cannot admit a booking on a future date')
                now=dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=7)
                sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(TODAY),now.hour,booking))
                confirm(payload,False);check(not sql('SELECT * FROM BookingCheckIns'),'Staff must explicitly verify name and booking details')
                admin.request(admin_page+f'?id={booking}');check(not sql('SELECT * FROM BookingCheckIns'),'Opening a lookup link has no check-in side effects')
                token=admin.token(admin_page)
                def race(_):return admin.clone().request('/Admin/ConfirmCheckIn',{'Code':payload,'IdentityVerified':'true','__RequestVerificationToken':token})
                with ThreadPoolExecutor(max_workers=2) as pool:responses=list(pool.map(race,range(2)))
                check(all(r[0]==200 for r in responses) and len(sql('SELECT * FROM BookingCheckIns'))==1,'Concurrent scans can create only one check-in record')
                saved=sql('SELECT BookingId,CheckedInAt,CheckedInBy,Method FROM BookingCheckIns')[0]
                check(saved[0]==booking and saved[2:]==(1,'Qr'),'Check-in records the authenticated staff member, time and QR method')
                confirm(number(booking));check(sql('SELECT BookingId,CheckedInAt,CheckedInBy,Method FROM BookingCheckIns')[0]==saved,'Repeated check-in cannot rewrite the original audit')
                check('เช็กอินแล้ว' in __import__('html').unescape(member.request(page)[2]) and 'เช็กอินแล้ว' in __import__('html').unescape(member.request('/Bookings/History')[2]),'Customer pass and history show the live check-in status')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(date),booking))
                member.post('/Accounting/RequestRefund',{'BookingId':booking,'Reason':'Attempt refund after admission'},token_page='/Accounting')
                check(not sql('SELECT * FROM BookingRefunds'),'Checked-in booking cannot request a refund even if its date is moved forward')
                sql("INSERT INTO BookingRefunds(BookingId,AmountSatang,Reason,RequestedAt) SELECT Id,NetSatang,'Pre-existing refund request',CreatedAt FROM Bookings WHERE Id=?",(booking,))
                refund=sql('SELECT Id FROM BookingRefunds')[0][0]
                admin.post(f'/Admin/ReviewRefund/{refund}',{'approve':'true'},token_page='/Admin/Refunds')
                check(sql('SELECT Status FROM BookingRefunds')[0][0]=='Pending' and sql('SELECT Status FROM Bookings WHERE Id=?',(booking,))[0][0]=='Confirmed','An earlier refund request cannot cancel a booking after check-in')
                manual=book(12);fund_and_pay(member,manual);admin.post(f'/Admin/ConfirmBooking/{manual}',{},token_page=f'/Admin/Booking/{manual}')
                sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(TODAY),now.hour,manual))
                confirm(number(manual));check(sql('SELECT Method FROM BookingCheckIns WHERE BookingId=?',(manual,))[0][0]=='Code','Staff can check in by backup booking reference when scanning fails')
                unpaid=book(13);admin.post(f'/Admin/ConfirmBooking/{unpaid}',{},token_page=f'/Admin/Booking/{unpaid}')
                sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(TODAY),now.hour,unpaid))
                confirm(number(unpaid));check(not sql('SELECT * FROM BookingCheckIns WHERE BookingId=?',(unpaid,)),'Unpaid booking cannot enter through the manual fallback')
                sql("UPDATE Bookings SET NetSatang=0 WHERE Id=?",(unpaid,))
                confirm(number(unpaid));check(sql('SELECT Method FROM BookingCheckIns WHERE BookingId=?',(unpaid,))[0][0]=='Code','Confirmed zero-total booking can check in without a cash debit')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(date),unpaid));member.post(f'/Bookings/Cancel/{unpaid}',{},token_page=f'/Bookings/Result/{unpaid}')
                check(sql('SELECT Status FROM Bookings WHERE Id=?',(unpaid,))[0][0]=='Confirmed','Checked-in free bookings cannot be cancelled to restore their slot')
                blocked=book(15);fund_and_pay(member,blocked);admin.post(f'/Admin/ConfirmBooking/{blocked}',{},token_page=f'/Admin/Booking/{blocked}')
                for day,status in [(TODAY-dt.timedelta(days=1),'Confirmed'),(TODAY,'Cancelled'),(TODAY,'Pending')]:
                    sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1,Status=? WHERE Id=?',(str(day),now.hour,status,blocked));confirm(number(blocked))
                    check(not sql('SELECT * FROM BookingCheckIns WHERE BookingId=?',(blocked,)),'Past, cancelled or unapproved bookings cannot be checked in')
                sql("UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1,Status='Confirmed' WHERE Id=?",(str(TODAY),23 if now.hour<22 else 0,blocked))
                confirm(number(blocked));check(not sql('SELECT * FROM BookingCheckIns WHERE BookingId=?',(blocked,)),'A booking outside its admission time cannot be checked in')
                stop();start()
                check(sql('SELECT BookingId,CheckedInAt,CheckedInBy,Method FROM BookingCheckIns WHERE BookingId=?',(booking,))[0]==saved and sql('SELECT Token FROM BookingEntryPasses WHERE BookingId=?',(booking,))[0][0]==payload[10:],'Restart preserves opaque passes and original staff audit')
                check(sql('PRAGMA foreign_key_check')==[],'Check-in schema retains valid relationships')
                stop();spec=importlib.util.spec_from_file_location('entry_export',ROOT/'Database/export_sqlite.py');exporter=importlib.util.module_from_spec(spec);spec.loader.exec_module(exporter)
                counts=exporter.export(database,Path(directory)/'entry.sql');check(counts['BookingEntryPasses']>0 and counts['BookingCheckIns']==3,'Database export preserves passes and check-in audit')
                print('ALL CHECK-IN CHECKS PASSED',flush=True)
            except Exception:
                log.flush();print((Path(directory)/'server.log').read_text(encoding='utf-8')[-8000:]);raise
            finally:stop()


if __name__=='__main__':run()
