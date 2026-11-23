"""Accounting and full booking refund integration checks; disposable SQLite only."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import base64
import csv
import datetime as dt
import html
import io
import json
import os
import socket
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, DLL, ROOT, TODAY, PASSWORD, check, legacy_payment


def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-accounting-') as directory:
        temp=Path(directory); db=temp/'test.db'
        base=f'http://127.0.0.1:{port()}'; admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(db),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Demo__AutomaticTopUps':'true','Logging__LogLevel__Default':'Warning'}
        def sql(query,args=()):
            with closing(sqlite3.connect(db)) as c:
                rows=c.execute(query,args).fetchall();c.commit();return rows
        server=None
        with (temp/'server.log').open('w',encoding='utf-8') as log:
            def start():
                nonlocal server
                server=subprocess.Popen(['dotnet',str(DLL),'--urls',base+';'+admin_base],cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
                for _ in range(100):
                    try:
                        if Browser(base).request('/')[0]==200:return
                    except OSError:pass
                    if server.poll() is not None:raise RuntimeError('Host exited')
                    time.sleep(.1)
                raise RuntimeError('Host did not start')
            def stop():
                if server and server.poll() is None:server.terminate();server.wait(timeout=10)
            try:
                start()
                member=Browser(base);other=Browser(base);admin=Browser(admin_base)
                for b,name in [(member,'finance'),(other,'private')]:
                    b.post('/Account/Register',{'Name':name,'Email':name+'@example.com','Password':PASSWORD,'ConfirmPassword':PASSWORD})
                    b.login(name+'@example.com')
                admin.login('admin@courtbook.local','CourtBook123!')
                for route in ['/Admin/Accounting','/Admin/Expenses','/Admin/Refunds']:
                    check(admin.request(route)[0]==200 and member.request(route)[0]==404,'Admin accounting page renders and is isolated: '+route)
                check(member.request('/Accounting')[0]==200 and Browser(base).request('/Accounting')[1]=='/Account/Login','Customer account requires authentication')
                check(admin.request('/Accounting')[0]==404,'Admin portal blocks member accounting')
                for route in ['/Accounting','/Accounting/Export']:
                    check(member.request(route+'?from=bad')[0]==400 and member.request(route+'?from=2020-01-01&to=2026-01-01')[0]==400,'Dates are validated: '+route)
                png=base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
                member.upload('/Wallet/TopUp',{'Amount':1000,'TransferReference':'FIN-TOPUP'},png,file_field='Slip',token_page='/Wallet')
                admin.post('/Admin/ReviewTopUp/1',{'approve':'true'},token_page='/Admin/TopUps')
                _,checkout,_=member.post('/Wallet/StartDemoPayment',{'Amount':200},token_page='/Wallet')
                member.post('/Wallet/CompleteDemoPayment',{'id':checkout.rsplit('/',1)[1],'outcome':'Paid'},token_page=checkout)
                date=TODAY+dt.timedelta(days=5)
                def book(browser,hour):
                    status,path,body=browser.post('/Bookings/Create',{'CourtId':1,'Date':str(date),'Hours':[hour]})
                    assert '/Bookings/Result/' in path,(status,path,body[:200])
                    return int(path.rsplit('/',1)[1])
                direct=book(member,8);wallet=book(member,9);unpaid=book(member,10);foreign=book(other,11)
                admin.post(f'/Admin/ConfirmBooking/{direct}',{},token_page=f'/Admin/Booking/{direct}')
                legacy_payment(db,direct,png+b'historical-accounting','LEGACY-FIN-DIRECT')
                admin.post(f'/Admin/ConfirmPayment/{direct}',{'Reference':'FIN-DIRECT'},token_page=f'/Admin/Booking/{direct}')
                member.post(f'/Bookings/PayWallet/{wallet}',{},token_page='/Wallet')
                admin.post('/Admin/Plans',{'Id':'monthly','Price':100,'DiscountPercent':10,'IsActive':'true'})
                member.post('/Wallet/Buy',{'planId':'monthly','expectedPriceSatang':10000,'expectedDiscountPercent':10,'expectedMonths':1},token_page='/Home/Membership')
                due=sql('SELECT NetSatang FROM Bookings WHERE Id=?',(wallet,))[0][0]
                direct_due=sql('SELECT NetSatang FROM Bookings WHERE Id=?',(direct,))[0][0]
                def balance():return sql('SELECT SUM(AmountSatang) FROM WalletEntries')[0][0]
                check(balance()==120000-due-10000,'Wallet charges and membership purchase have exact satang amounts')
                expense={'Date':str(TODAY),'Category':'Maintenance','Description':'=SUM(1,2)','Payee':'=supplier','Amount':50,'Method':'Transfer','Reference':'EXP-1'}
                check(admin.request('/Admin/Expenses',expense)[0]==400,'Expenses require CSRF')
                for change in [{'Amount':'0'},{'Amount':'1.001'},{'Category':'Invalid'},{'Date':str(TODAY+dt.timedelta(days=1))}]:
                    admin.post('/Admin/Expenses',{**expense,**change})
                check(sql('SELECT COUNT(*) FROM Expenses')[0][0]==0,'Invalid expenses never enter the ledger')
                admin.post('/Admin/Expenses',expense)
                admin.post('/Admin/Expenses',{**expense,'Description':'Duplicate entry','Amount':20,'Reference':'EXP-2'})
                admin.post('/Admin/VoidExpense/2',{'reason':'Entered twice'},token_page='/Admin/Expenses')
                check(sql('SELECT Status,VoidReason,VoidedBy IS NOT NULL FROM Expenses WHERE Id=2')[0]==('Void','Entered twice',1),'Voided expense preserves audit and reason')
                check(admin.post('/Admin/VoidExpense/2',{'reason':'Entered twice'},token_page='/Admin/Expenses')[0]==404,'Voiding an expense cannot run twice')
                def export(browser,route):
                    with browser.opener.open(browser.base+route) as response:
                        raw=response.read();check(raw.startswith(b'\xef\xbb\xbf'),'CSV includes UTF-8 BOM')
                        return list(csv.reader(io.StringIO(raw.decode('utf-8-sig'))))
                rows=export(admin,'/Admin/AccountingExport')
                def total(label):return sum(round(float(r[4])*100) for r in rows[1:] if r[1]==label and r[5]!='Void')
                check(total('ชำระค่าจอง')==due+direct_due and total('ซื้อแพ็ก')==10000 and total('เติมเงินผ่านธนาคาร')==100000 and total('เครดิตทดลอง')==20000 and total('รายจ่าย')==5000,'Accounting separates booking, plan, bank top-up, demo and active expense totals')
                check(any(r[3].startswith("'=SUM") for r in rows[1:]),'CSV neutralizes spreadsheet formulas')
                page=html.unescape(admin.request('/Admin/Accounting')[2])
                expected=f'{(100000+direct_due-5000)/100:,.2f}'
                check(expected in page,'Cash difference counts external receipts once and excludes wallet charges and demo credit')
                admin.post(f'/Admin/ConfirmBooking/{foreign}',{},token_page=f'/Admin/Booking/{foreign}')
                legacy_payment(db,foreign,png+b'historical-private','LEGACY-PRIVATE')
                admin.post(f'/Admin/ConfirmPayment/{foreign}',{'Reference':'PRIVATE-RECEIPT'},token_page=f'/Admin/Booking/{foreign}')
                foreign_ref=f'CB{foreign:06d}'
                foreign_snapshot=json.loads(sql('SELECT SnapshotJson FROM PaymentReceipts WHERE BookingId=?',(foreign,))[0][0])
                previous=TODAY-dt.timedelta(days=1)
                foreign_snapshot['PaidAt']=str(previous)+'T16:59:59Z'
                sql('UPDATE PaymentReceipts SET SnapshotJson=? WHERE BookingId=?',(json.dumps(foreign_snapshot),foreign))
                range_route=f'/Admin/AccountingExport?from={TODAY}&to={TODAY}'
                check(not any(r[2]==foreign_ref for r in export(admin,range_route)[1:]),'Thai date excludes the second before midnight')
                foreign_snapshot['PaidAt']=str(previous)+'T17:00:00Z'
                sql('UPDATE PaymentReceipts SET SnapshotJson=? WHERE BookingId=?',(json.dumps(foreign_snapshot),foreign))
                check(any(r[2]==foreign_ref for r in export(admin,range_route)[1:]),'Thai date includes midnight at the start boundary')
                member_rows=export(member,'/Accounting/Export?userId=999')
                check(not any(r[2]==foreign_ref for r in member_rows[1:]),'Forged owner query cannot export another paid booking')
                check(all(r[1]!='รายจ่าย' for r in member_rows[1:]) and len(member_rows[0])==8,'Customer CSV excludes staff expense records and other member columns')
                for id in [unpaid,foreign]:
                    member.post('/Accounting/RequestRefund',{'BookingId':id,'Reason':'Cancel session'},token_page='/Accounting')
                check(sql('SELECT COUNT(*) FROM BookingRefunds')[0][0]==0,'Unpaid and foreign booking refund requests are rejected')
                check(member.request('/Accounting/RequestRefund',{'BookingId':wallet,'Reason':'Cancel session'})[0]==400,'Refund request requires CSRF')
                member.post('/Accounting/RequestRefund',{'BookingId':wallet,'Reason':'Cancel session','AmountSatang':1},token_page='/Accounting')
                member.post('/Accounting/RequestRefund',{'BookingId':wallet,'Reason':'Repeat session'},token_page='/Accounting')
                check(sql('SELECT COUNT(*),MAX(AmountSatang) FROM BookingRefunds')[0]==(1,due),'Full refund amount comes from server and duplicate requests are blocked')
                check('Cancel session' not in other.request('/Accounting')[2],'Refund requests stay private to owner')
                snapshot=sql('SELECT SnapshotJson FROM PaymentReceipts WHERE BookingId=?',(wallet,))[0][0]
                before=balance()
                sql("CREATE TRIGGER fail_refund BEFORE INSERT ON WalletEntries WHEN NEW.Description LIKE 'คืนค่าจอง %' BEGIN SELECT RAISE(ABORT,'forced failure'); END")
                check(admin.post('/Admin/ReviewRefund/1',{'approve':'true'},token_page='/Admin/Refunds')[0]==500,'Forced wallet failure is surfaced')
                check(balance()==before and sql('SELECT Status,WalletEntryId FROM BookingRefunds')[0]==('Pending',None) and sql('SELECT Status FROM Bookings WHERE Id=?',(wallet,))[0][0]!='Cancelled','Failure rolls back refund credit and cancellation together')
                sql('DROP TRIGGER fail_refund')
                token=admin.token('/Admin/Refunds')
                with ThreadPoolExecutor(max_workers=2) as pool:
                    list(pool.map(lambda b:b.request('/Admin/ReviewRefund/1',{'approve':'true','note':'Approved full refund','__RequestVerificationToken':token}),[admin.clone(),admin.clone()]))
                check(balance()==before+due and sql('SELECT COUNT(*) FROM WalletEntries WHERE Description LIKE ?',('คืนค่าจอง %',))[0][0]==1,'Concurrent refund approvals credit exactly once')
                check(sql('SELECT Status FROM Bookings WHERE Id=?',(wallet,))[0][0]=='Cancelled' and sql('SELECT COUNT(*) FROM BookedSlots WHERE BookingId=?',(wallet,))[0][0]==0,'Approved refund cancels booking and releases slots')
                check(sql('SELECT SnapshotJson FROM PaymentReceipts WHERE BookingId=?',(wallet,))[0][0]==snapshot,'Original paid receipt snapshot remains unchanged')
                receipt=html.unescape(member.request(f'/Receipts/Details/RC-BK-{wallet:06d}')[2])
                check('คืนเข้ากระเป๋าแล้ว' in receipt and 'ยกเลิกการจองแล้ว' in receipt,'Receipt shows live refund and cancelled admission status')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(previous),direct))
                member.post('/Accounting/RequestRefund',{'BookingId':direct,'Reason':'Past session request'},token_page='/Accounting')
                check(sql('SELECT COUNT(*) FROM BookingRefunds')[0][0]==1,'Already started bookings cannot request a refund')
                sql('UPDATE Bookings SET PlayDate=? WHERE Id=?',(str(date),direct))
                member.post('/Accounting/RequestRefund',{'BookingId':direct,'Reason':'Another cancellation'},token_page='/Accounting')
                admin.post('/Admin/ReviewRefund/2',{'approve':'false','note':'Terms reviewed; booking remains valid'},token_page='/Admin/Refunds')
                admin.post('/Admin/ReviewRefund/2',{'approve':'true'},token_page='/Admin/Refunds')
                check(balance()==before+due and sql('SELECT Status FROM BookingRefunds WHERE Id=2')[0][0]=='Rejected','Rejection keeps funds unchanged and cannot later approve the same request')
                rows=export(member,'/Accounting/Export')
                check(sum(round(float(r[4])*100) for r in rows[1:] if r[1]=='คืนค่าจองเข้ากระเป๋า')==due,'Approved refund appears once in customer accounting')
                check(sql('PRAGMA foreign_key_check')==[],'Accounting foreign keys remain valid')
                stop();start()
                check(balance()==before+due and sql('SELECT COUNT(*) FROM BookingRefunds')[0][0]==2 and sql('SELECT COUNT(*) FROM Expenses')[0][0]==2,'Accounting and refund audit survive restart')
            except Exception:
                log.flush();print((temp/'server.log').read_text(encoding='utf-8')[-7000:]);raise
            finally:stop()
    print('Accounting integration checks passed.')


if __name__=='__main__':run()
