"""Time-based stock, physical handovers, partial/damaged returns and concurrent booking checks."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import datetime as dt
import json
import os
import sqlite3
import subprocess
import tempfile
import time
import urllib.parse
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check, fund_and_pay
from product_smoke import port
from damage_smoke import PNG


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-inventory-') as directory:
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
                start();admin=Browser(admin_base);admin.login('admin@courtbook.local','CourtBook123!')
                member=Browser(base);other=Browser(base)
                for browser,email in [(member,'stock@example.com'),(other,'other-stock@example.com')]:
                    browser.post('/Account/Register',{'Name':email,'Email':email,'Password':PASSWORD,'ConfirmPassword':PASSWORD});browser.login(email)
                courts=[r[0] for r in sql('SELECT Id FROM Courts WHERE SportTypeId=1 ORDER BY Id')]
                if len(courts)<2:
                    admin.post('/Admin/Court/0',{'Id':0,'Name':'Second inventory court','Price':300,'SportTypeId':1,'IsActive':'true'})
                    courts=[r[0] for r in sql('SELECT Id FROM Courts WHERE SportTypeId=1 ORDER BY Id')]
                date=TODAY+dt.timedelta(days=3);stock_route='/Admin/EquipmentStock/racket'
                def stock(total,repair=0,revision=None):
                    if revision is None:revision=sql("SELECT Revision FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]
                    return admin.post(stock_route,{'TotalQuantity':total,'RepairQuantity':repair,'Revision':revision,'Note':'Verified physical inventory count'})
                def fields(court,hours,quantity=1,date=date):return {'CourtId':court,'Date':str(date),'Hours':hours,'Equipment[racket]':quantity}
                def book(court,hours,quantity=1,date=date,browser=member):
                    response=browser.post('/Bookings/Create',fields(court,hours,quantity,date));return int(response[1].rsplit('/',1)[1]) if response[1].startswith('/Bookings/Result/') else None
                def quote(court,hours,quantity=1,date=date):return member.request('/Bookings/Quote?'+urllib.parse.urlencode(fields(court,hours,quantity,date),doseq=True))
                def counts(booking):return sql("SELECT IssuedQuantity,GoodQuantity,DamagedQuantity,Revision FROM EquipmentLoans WHERE BookingId=? AND EquipmentId='racket'",(booking,))[0]
                check(quote(courts[0],[10])[0]==400,'Uncounted equipment cannot be rented with invented stock')
                check(member.request('/Admin/Inventory')[0]==404 and admin.request('/Admin/Inventory')[0]==200,'Inventory is restricted to admin portal')
                check(admin.request(stock_route,{'TotalQuantity':3,'RepairQuantity':0,'Revision':0,'Note':'No CSRF'})[0]==400,'Stock updates require CSRF')
                stock(3,1);check(sql("SELECT TotalQuantity,RepairQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0]==(3,1),'Counted stock separates total and repair quantities')
                a=book(courts[0],[10],2);check(a is not None,'A booking reserves all remaining rentable stock')
                check(quote(courts[1],[10])[0]==400 and book(courts[1],[10]) is None,'Reservations across courts cannot exceed shared equipment stock')
                b=book(courts[1],[11],2);check(b is not None,'Adjacent non-overlapping bookings reuse the same equipment')
                check(quote(courts[1],[9,10])[0]==400,'Availability uses the busiest point of a selected interval')
                available=json.loads(member.request('/Bookings/EquipmentAvailability?'+urllib.parse.urlencode({'courtId':courts[1],'date':str(date),'hours':[10]},doseq=True))[2])
                check(next(e for e in available if e['id']=='racket')['available']==0,'Availability endpoint reports exact stock for the selected time')
                stock(2,1);check(sql("SELECT TotalQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==3,'Stock cannot be reduced below booked capacity')
                stock(5,1,revision=0);check(sql("SELECT TotalQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==3,'Stale stock forms cannot overwrite a newer count')
                member.post(f'/Bookings/Cancel/{a}',{},token_page=f'/Bookings/Result/{a}')
                check(quote(courts[1],[10],2)[0]==200,'Cancelling a booking releases its equipment reservation')
                c=book(courts[0],[12],2);stock(3,1)
                check(sql("SELECT TotalQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==3,'Non-overlapping reservations are not incorrectly added together')
                race_date=date+dt.timedelta(days=1);token=member.token('/Bookings/Create')
                def race(court):return member.clone().request('/Bookings/Create',{**fields(court,[10],2,race_date),'__RequestVerificationToken':token})
                with ThreadPoolExecutor(max_workers=2) as pool:responses=list(pool.map(race,courts[:2]))
                check(sum(r[1].startswith('/Bookings/Result/') for r in responses)==1,'Concurrent requests cannot reserve the final items twice')
                active=book(courts[0],[14],2,date+dt.timedelta(days=2));route=f'/Admin/EquipmentHandover/{active}'
                admin.post(f'/Admin/ConfirmBooking/{active}',{},token_page=f'/Admin/Booking/{active}')
                fund_and_pay(member,active)
                issue={'EquipmentId':'racket','Quantity':2,'Revision':0,'Note':'Checked intact before handing over'}
                admin.post(f'/Admin/IssueEquipment/{active}',issue,token_page=route)
                check(counts(active)==(0,0,0,0),'Equipment cannot be issued before its booked session')
                # Move only this isolated fixture into the current hour to test real handovers at any wall-clock time.
                now=dt.datetime.now(dt.timezone.utc)+dt.timedelta(hours=7)
                sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(TODAY),now.hour,active))
                check(other.request(f'/Bookings/EquipmentHandover/{active}?userId=1')[0]==404,'Handover details remain private to the booking customer')
                check(admin.request(f'/Admin/IssueEquipment/{active}',issue)[0]==400,'Equipment handovers require CSRF')
                admin.upload(f'/Admin/IssueEquipment/{active}',issue,PNG,token_page=route)
                check(counts(active)==(2,0,0,1),'Admin issues exactly the reserved quantity with condition evidence')
                admin.post(f'/Admin/IssueEquipment/{active}',issue,token_page=route)
                check(counts(active)==(2,0,0,1),'Replaying a handover form cannot issue twice')
                admin.post(f'/Admin/ConfirmBooking/{b}',{},token_page=f'/Admin/Booking/{b}')
                fund_and_pay(member,b)
                sql('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(TODAY),now.hour,b))
                admin.post(f'/Admin/IssueEquipment/{b}',issue,token_page=f'/Admin/EquipmentHandover/{b}')
                check(counts(b)==(0,0,0,0),'Physical handover cannot issue items already held by another customer')
                sql('UPDATE Bookings SET PlayDate=?,StartHour=11 WHERE Id=?',(str(date),b))
                # A future refund request must never cancel a booking whose physical items are still out.
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(date+dt.timedelta(days=2)),active))
                member.post('/Accounting/RequestRefund',{'BookingId':active,'Reason':'Cannot attend the booked session'},token_page='/Accounting')
                refund=sql('SELECT Id FROM BookingRefunds WHERE BookingId=?',(active,))[0][0]
                admin.post(f'/Admin/ReviewRefund/{refund}',{'approve':'true','note':'Review issued items first'},token_page='/Admin/Refunds')
                check(sql('SELECT Status FROM BookingRefunds WHERE Id=?',(refund,))[0][0]=='Pending' and sql('SELECT Status FROM Bookings WHERE Id=?',(active,))[0][0]=='Confirmed','Refund approval cannot cancel a booking with unreturned equipment')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY),active))
                image_id=sql("SELECT Id FROM EquipmentMovements WHERE BookingId=? AND Kind='Issue'",(active,))[0][0]
                with member.opener.open(base+f'/Bookings/EquipmentMovementImage/{image_id}') as response:check(response.read()==PNG,'Customer can inspect their own handover evidence')
                check(other.request(f'/Bookings/EquipmentMovementImage/{image_id}')[0]==404,'Another customer cannot inspect handover photos')
                # After the scheduled end, physical items remain unavailable until returned.
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(TODAY-dt.timedelta(days=1)),active))
                check(quote(courts[0],[15],1,date+dt.timedelta(days=5))[0]==400,'Overdue physical items block future rentals until actually returned')
                bad_return={'EquipmentId':'racket','GoodQuantity':2,'DamagedQuantity':1,'Revision':1,'Note':'Attempt to return too many items'}
                admin.post(f'/Admin/ReturnEquipment/{active}',bad_return,token_page=route)
                check(counts(active)==(2,0,0,1),'Return quantities cannot exceed outstanding physical items')
                partial={'EquipmentId':'racket','GoodQuantity':1,'DamagedQuantity':0,'Revision':1,'Note':'One item returned in good condition'}
                admin.post(f'/Admin/ReturnEquipment/{active}',partial,token_page=route)
                check(counts(active)==(2,1,0,2),'Partial good returns release only the actual returned quantity')
                damage={'EquipmentId':'racket','GoodQuantity':0,'DamagedQuantity':1,'Revision':2,'Note':'Handle cracked when returned by customer'}
                before=sql('SELECT COUNT(*) FROM DamageReports')[0][0]
                # A downstream report failure must not record a return or alter repair stock.
                sql("CREATE TRIGGER fail_inventory_damage BEFORE INSERT ON DamageReports BEGIN SELECT RAISE(ABORT,'report failure'); END")
                response=admin.post(f'/Admin/ReturnEquipment/{active}',damage,token_page=route)
                check(response[0]==500 and counts(active)==(2,1,0,2) and sql("SELECT RepairQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==1,'Failure rolls back return, repair stock and linked damage report together')
                sql('DROP TRIGGER fail_inventory_damage')
                admin.upload(f'/Admin/ReturnEquipment/{active}',damage,PNG,token_page=route)
                check(counts(active)==(2,1,1,3) and sql("SELECT RepairQuantity FROM EquipmentStock WHERE EquipmentId='racket'")[0][0]==2,'Damaged returns leave physical custody and enter repair stock')
                check(sql('SELECT COUNT(*) FROM DamageReports')[0][0]==before+1 and sql('SELECT COUNT(*) FROM DamageEvidence')[0][0]==1 and not sql('SELECT * FROM DamageClaims'),'Damaged returns create a report with evidence without charging the customer')
                check('จำนวนพร้อมใช้ไม่พอ' in __import__('html').unescape(admin.request('/Admin/Inventory')[2]),'Real damage can be recorded even when it causes a shortage, with a visible warning')
                admin.post(f'/Admin/ReturnEquipment/{active}',damage,token_page=route)
                check(counts(active)==(2,1,1,3),'Replaying a return form cannot add repair stock twice')
                stock(3,0);check(quote(courts[0],[15],2,date+dt.timedelta(days=5))[0]==200,'Recording completed repairs restores rentable stock')
                # Existing booking snapshots are imported on upgrade without inventing physical transfers.
                saved=sql('SELECT * FROM EquipmentLoans ORDER BY BookingId,EquipmentId')
                sql('DELETE FROM EquipmentLoans WHERE BookingId=?',(c,));stop();start()
                restored=sql('SELECT * FROM EquipmentLoans ORDER BY BookingId,EquipmentId')
                check(restored==saved,'Restart backfills legacy reservations and preserves existing handover counts')
                check(sql('PRAGMA foreign_key_check')==[],'Inventory, damage and booking relationships remain valid')
                stop()
                import importlib.util
                spec=importlib.util.spec_from_file_location('inventory_export',ROOT/'Database/export_sqlite.py')
                exporter=importlib.util.module_from_spec(spec);spec.loader.exec_module(exporter)
                exported=exporter.export(database,Path(directory)/'inventory.sql')
                check(exported['EquipmentLoans']==len(saved) and exported['EquipmentMovements']>0,'Export preserves real inventory reservations, handovers and repair history')
                print('ALL INVENTORY CHECKS PASSED',flush=True)
            except Exception:
                log.flush();print((Path(directory)/'server.log').read_text(encoding='utf-8')[-8000:]);raise
            finally:stop()


if __name__=='__main__':run()
