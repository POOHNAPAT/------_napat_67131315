"""Staff checkout, outstanding returns, concurrency and persisted admission audit."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import datetime as dt
import html
import importlib.util
import os
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check, fund_and_pay
from product_smoke import port


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-checkout-') as directory:
        database=Path(directory)/'test.db';base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(database),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Logging__LogLevel__Default':'Warning'}
        def sql(statement,args=()):
            with closing(sqlite3.connect(database)) as c:
                rows=c.execute(statement,args).fetchall();c.commit();return rows
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
                start();member=Browser(base);admin=Browser(admin_base)
                member.post('/Account/Register',{'Name':'Checkout Player','Email':'checkout@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD});member.login('checkout@example.com')
                admin.login('admin@courtbook.local','CourtBook123!')
                admin.post('/Admin/EquipmentStock/racket',{'TotalQuantity':4,'RepairQuantity':0,'Revision':0,'Note':'Counted intact rackets'},token_page='/Admin/EquipmentStock/racket')
                now=dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=7)
                def book(hour,equipment=2):
                    fields={'CourtId':1,'Date':str(TODAY+dt.timedelta(days=5)),'Hours':[hour]}
                    if equipment:fields['Equipment[racket]']=equipment
                    response=member.post('/Bookings/Create',fields);id=int(response[1].rsplit('/',1)[1])
                    fund_and_pay(member,id);admin.post(f'/Admin/ConfirmBooking/{id}',{},token_page=f'/Admin/Booking/{id}')
                    sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=2 WHERE Id=?',(str(TODAY),now.hour,id))
                    return id
                def checkout(id,verified=True):return admin.post(f'/Admin/ConfirmCheckOut/{id}',{'EquipmentVerified':str(verified).lower()},token_page=f'/Admin/Booking/{id}')
                def admit(id):admin.post('/Admin/ConfirmCheckIn',{'Code':f'CB{id:06d}','IdentityVerified':'true'},token_page='/Admin/CheckIn')
                def issue(id,quantity):
                    return admin.post(f'/Admin/IssueEquipment/{id}',{'EquipmentId':'racket','Quantity':quantity,'Revision':0,'Note':'Equipment intact on issue'},token_page=f'/Admin/EquipmentHandover/{id}')
                def receive(id,good,damaged,revision):
                    return admin.post(f'/Admin/ReturnEquipment/{id}',{'EquipmentId':'racket','GoodQuantity':good,'DamagedQuantity':damaged,'Revision':revision,'Note':'Checked returned equipment'},token_page=f'/Admin/EquipmentHandover/{id}')
                first=book(10)
                checkout(first);check(not sql('SELECT * FROM BookingCheckOuts'),'Unadmitted booking cannot check out')
                check(member.post(f'/Admin/ConfirmCheckOut/{first}',{'EquipmentVerified':'true'},token_page='/Wallet')[0]==404,'Customer cannot confirm staff checkout')
                check(admin.request(f'/Admin/ConfirmCheckOut/{first}',{'EquipmentVerified':'true'})[0]==400,'Checkout requires CSRF')
                admit(first);issue(first,2)
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY-dt.timedelta(days=1)),first))
                checkout(first)
                check(not sql('SELECT * FROM BookingCheckOuts') and 'หมดเวลา · รอคืนอุปกรณ์' in html.unescape(member.request('/Bookings/History')[2]),'Elapsed sessions keep outstanding returns visible and block checkout')
                receive(first,1,0,1);checkout(first)
                check(not sql('SELECT * FROM BookingCheckOuts'),'Partial return cannot close the session')
                receive(first,0,1,2);checkout(first,False)
                check(not sql('SELECT * FROM BookingCheckOuts') and len(sql('SELECT * FROM DamageReports'))==1,'Damaged return creates a report; checkout still requires explicit staff verification')
                token=admin.token(f'/Admin/Booking/{first}')
                def race(_):return admin.clone().request(f'/Admin/ConfirmCheckOut/{first}',{'EquipmentVerified':'true','__RequestVerificationToken':token})
                with ThreadPoolExecutor(max_workers=2) as pool:responses=list(pool.map(race,range(2)))
                check(all(r[0]==200 for r in responses) and len(sql('SELECT * FROM BookingCheckOuts'))==1,'Concurrent checkout creates exactly one immutable audit record')
                saved=sql('SELECT * FROM BookingCheckOuts')[0];checkout(first)
                check(sql('SELECT * FROM BookingCheckOuts')[0]==saved and saved[2]==1,'Repeated checkout preserves authenticated staff and original time')
                check('เช็กเอาต์แล้ว' in html.unescape(member.request(f'/Bookings/Result/{first}')[2]) and 'เช็กเอาต์แล้ว' in html.unescape(member.request(f'/Receipts/Details/RC-BK-{first:06d}')[2]),'Customer history, booking and receipt show the completed checkout')
                second=book(12);admit(second);issue(second,1)
                check('id="entry-qr"' in member.request(f'/Bookings/EntryPass/{second}')[2],'Live admitted session retains its entry page before checkout')
                receive(second,1,0,1);checkout(second)
                admin.post(f'/Admin/IssueEquipment/{second}',{'EquipmentId':'racket','Quantity':1,'Revision':2,'Note':'Attempt late issue after closing'},token_page=f'/Admin/EquipmentHandover/{second}')
                check(sql('SELECT IssuedQuantity FROM EquipmentLoans WHERE BookingId=?',(second,))[0][0]==1,'Early checkout blocks issuing any unissued reservation afterward')
                check(member.request(f'/Bookings/EntryQr/{second}')[0]==404 and 'id="entry-qr"' not in member.request(f'/Bookings/EntryPass/{second}')[2],'Early checkout immediately stops admission QR access')
                admit(second);check(sql('SELECT COUNT(*) FROM BookingCheckIns WHERE BookingId=?',(second,))[0][0]==1,'Checked-out session cannot check in again')
                third=book(14,0);admit(third);checkout(third)
                check(sql('SELECT COUNT(*) FROM BookingCheckOuts WHERE BookingId=?',(third,))[0][0]==1,'Session without rented equipment can check out after staff confirmation')
                admin.post('/Admin/EquipmentStock/racket',{'TotalQuantity':1,'RepairQuantity':1,'Revision':2,'Note':'Count after all customers checked out'},token_page='/Admin/EquipmentStock/racket')
                check(sql("SELECT TotalQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==1,'Early checkout releases remaining unissued equipment reservations')
                check(sql('PRAGMA foreign_key_check')==[],'Checkout audit retains valid relationships')
                snapshot=sql('SELECT * FROM BookingCheckOuts ORDER BY BookingId');stop();start()
                check(sql('SELECT * FROM BookingCheckOuts ORDER BY BookingId')==snapshot,'Existing database upgrades and restart preserve checkout history')
                stop();spec=importlib.util.spec_from_file_location('checkout_export',ROOT/'Database/export_sqlite.py');exporter=importlib.util.module_from_spec(spec);spec.loader.exec_module(exporter)
                counts=exporter.export(database,Path(directory)/'checkout.sql')
                check(counts['BookingCheckOuts']==3,'Export preserves checkout audit with staff and timestamps')
                print('ALL CHECKOUT CHECKS PASSED',flush=True)
            finally:stop()


if __name__=='__main__':run()
