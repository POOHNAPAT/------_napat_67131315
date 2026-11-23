"""Damage evidence, consent, review, wallet payment and rollback on an isolated MVC host."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
import base64
import datetime as dt
import json
import os
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check
from product_smoke import port

PNG=base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-damage-') as directory:
        database=Path(directory)/'test.db'
        base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(database),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Logging__LogLevel__Default':'Warning'}
        def query(sql,args=()):
            with closing(sqlite3.connect(database)) as c:return c.execute(sql,args).fetchall()
        def execute(sql,args=()):
            with closing(sqlite3.connect(database)) as c:c.execute(sql,args);c.commit()
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
                start()
                admin=Browser(admin_base);admin.login('admin@courtbook.local','CourtBook123!')
                customer=Browser(base);other=Browser(base)
                for browser,email in [(customer,'damage@example.com'),(other,'other-damage@example.com')]:
                    browser.post('/Account/Register',{'Name':email,'Email':email,'Password':PASSWORD,'ConfirmPassword':PASSWORD});browser.login(email)
                user=query("SELECT Id FROM Users WHERE Email='damage@example.com'")[0][0]
                _,booking_path,_=customer.post('/Bookings/Create',{'CourtId':1,'Date':str(TODAY+dt.timedelta(days=2)),'Hours':[10]})
                booking=int(booking_path.rsplit('/',1)[1]);reports='/Admin/DamageReports';customer_reports='/Bookings/ReportDamage'
                report_route=f'/Admin/ReportDamage/{booking}'
                check(admin.request(report_route)[0]==200,'Admin can open damage reporting from a booking')
                admin.upload(report_route,{'Category':'Court','Description':'Broken surface after use'},PNG,file_field='Evidence')
                report=query('SELECT MAX(Id) FROM DamageReports')[0][0]
                photo=query('SELECT Id FROM DamageEvidence WHERE ReportId=?',(report,))[0][0]
                check(query('SELECT UserId,BookingId FROM DamageReports WHERE Id=?',(report,))[0]==(user,booking),'Admin report is assigned to the actual booking customer')
                check(other.request(f'/Damage/Evidence/{photo}?admin=true')[0]==404,'Another customer cannot read evidence or forge admin access')
                with customer.opener.open(base+f'/Damage/Evidence/{photo}') as response:
                    check(response.read()==PNG and response.headers['Cache-Control']=='no-store','Evidence bytes are private and served without caching')
                with admin.opener.open(admin_base+f'/Damage/Evidence/{photo}') as response:check(response.status==200,'Admin can inspect evidence')
                check(customer.request(f'/Damage/Pay/{report}',{'revision':1})[0]==400,'Damage payment requires CSRF')
                def assess(amount=100,revision=0,waive=False):
                    return admin.post(f'/Damage/Assess/{report}',{'Revision':revision,'Amount':amount,'Waive':str(waive).lower(),'Reason':'Surface repair: labour and materials'},token_page=reports)
                def respond(decision,revision,note='',browser=customer):
                    return browser.post(f'/Damage/Respond/{report}',{'Revision':revision,'Decision':decision,'Note':note},token_page=customer_reports)
                def state():return query('SELECT Revision,Status,AmountSatang FROM DamageClaims WHERE ReportId=?',(report,))[0]
                assess();check(state()==(1,'AwaitingCustomer',10000),'Assessment creates a customer response request without debiting money')
                check(admin.request(f'/Damage/Assess/{report}',{'Amount':1,'Revision':1,'Reason':'Missing CSRF token'})[0]==400,'Admin assessment requires CSRF')
                customer.post(f'/Damage/Assess/{report}',{'Amount':1,'Revision':1,'Reason':'Forged administrator assessment'},token_page=customer_reports)
                check(state()==(1,'AwaitingCustomer',10000),'Customer cannot assess or alter a charge')
                check(query('SELECT COUNT(*) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==0,'Assessment never automatically debits the customer')
                customer.post(f'/Damage/Pay/{report}',{'revision':1},token_page=customer_reports)
                check(state()[1]=='AwaitingCustomer','Payment is rejected before customer acceptance')
                respond('Accept',1,browser=other);check(state()[1]=='AwaitingCustomer','Another customer cannot accept an assessment')
                respond('Dispute',1,'x');check(state()[1]=='AwaitingCustomer','Disputes require a meaningful explanation')
                respond('Dispute',1,'This damage was present before use')
                check(state()[1]=='Disputed' and 'This damage was present' in admin.request(reports)[2],'Customer can dispute and admin sees the explanation')
                check(f'report-{report}' in admin.request(reports+'?status=Disputed')[2],'Admin filters by claim state')
                assess(80,1);check(state()==(2,'AwaitingCustomer',8000),'Reassessment changes amount and requires fresh consent')
                respond('Accept',1);check(state()[1]=='AwaitingCustomer','Stale acceptance cannot accept a revised amount')
                assess(1,1);check(state()==(2,'AwaitingCustomer',8000),'Stale admin forms cannot overwrite the latest assessment')
                respond('Accept',2);check(state()[1]=='AwaitingPayment','Customer accepts without an immediate debit')
                respond('Dispute',2,'Please review the repair estimate again');check(state()[1]=='Disputed','Customer may dispute before paying an accepted assessment')
                assess(75,2);respond('Accept',3)
                customer.post(f'/Damage/Pay/{report}',{'revision':3},token_page=customer_reports)
                check(state()[1]=='AwaitingPayment' and not query('SELECT * FROM DamageReceipts'),'Insufficient balance leaves claim unpaid with no receipt')
                _,checkout,_=customer.post('/Wallet/StartDemoPayment',{'Amount':500},token_page='/Wallet')
                customer.post('/Wallet/CompleteDemoPayment',{'id':checkout.rsplit('/',1)[1],'outcome':'Paid'},token_page=checkout)
                balance=query('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]
                execute("CREATE TRIGGER fail_damage_receipt BEFORE INSERT ON DamageReceipts BEGIN SELECT RAISE(ABORT,'receipt failure'); END")
                response=customer.post(f'/Damage/Pay/{report}',{'revision':3},token_page=customer_reports)
                check(response[0]==500 and state()[1]=='AwaitingPayment' and query('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==balance,'Receipt failure rolls back the payment, debit and claim together')
                execute('DROP TRIGGER fail_damage_receipt')
                # Independent HTTP clients race on the same accepted claim.
                token=customer.token(customer_reports)
                def pay(_):return customer.clone().request(f'/Damage/Pay/{report}',{'revision':3,'__RequestVerificationToken':token})
                with ThreadPoolExecutor(max_workers=2) as pool:responses=list(pool.map(pay,range(2)))
                check(all(r[0]==200 for r in responses) and state()[1]=='Paid','Concurrent payment requests complete safely')
                check(query('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==balance-7500 and query('SELECT COUNT(*) FROM DamageReceipts')[0][0]==1,'Concurrent retries debit exactly once and issue one receipt')
                receipt_id=f'RC-DM-{report:06d}';receipt_path=f'/Receipts/Details/{receipt_id}'
                check(customer.request(receipt_path)[0]==200 and other.request(receipt_path)[0]==404 and admin.request(f'/Admin/Receipt/{receipt_id}')[0]==200,'Damage receipt is available only to its customer and admin')
                check(receipt_id in customer.request('/Receipts')[2] and receipt_id in admin.request('/Admin/Accounting')[2],'Damage receipt appears in receipts and accounting')
                saved=query('SELECT SnapshotJson FROM DamageReceipts')[0][0]
                data=json.loads(saved);check(data['NetSatang']==7500 and data['Kind']=='Damage','Receipt snapshots the accepted repair cost')
                assess(100,3);respond('Dispute',3,'Trying to reopen after paying')
                admin.post(f'/Admin/UpdateDamageReport/{report}',{'Status':'Reported','AdminNote':'Bypass attempt'},token_page=reports)
                check(state()[1]=='Paid' and query('SELECT SnapshotJson FROM DamageReceipts')[0][0]==saved,'Paid assessments cannot be reopened or overwritten by legacy report actions')
                before=query('SELECT COUNT(*) FROM DamageEvidence')[0][0]
                admin.upload(f'/Damage/AddEvidence/{report}',{'Kind':'After'},PNG,token_page=reports)
                check(query('SELECT COUNT(*) FROM DamageEvidence')[0][0]==before,'Closed claims reject evidence changes')
                # Waivers and evidence requirements use another report from the same booking.
                admin.post(report_route,{'Category':'Court','Description':'Normal wear found on surface'})
                report=query('SELECT MAX(Id) FROM DamageReports')[0][0]
                assess();check(not query('SELECT * FROM DamageClaims WHERE ReportId=?',(report,)),'Charges require photographic evidence')
                invalid=admin.upload(f'/Damage/AddEvidence/{report}',{'Kind':'After'},b'<script>bad</script>',filename='fake.png',token_page=reports)
                check(not query('SELECT * FROM DamageEvidence WHERE ReportId=?',(report,)),'Spoofed image uploads are rejected by file signature')
                admin.upload(f'/Damage/AddEvidence/{report}',{'Kind':'Before','Caption':'Before use'},PNG,token_page=reports)
                photo=query('SELECT Id FROM DamageEvidence WHERE ReportId=?',(report,))[0][0]
                other.upload(f'/Damage/AddEvidence/{report}',{'Kind':'After'},PNG,token_page=customer_reports)
                check(query('SELECT COUNT(*) FROM DamageEvidence WHERE ReportId=?',(report,))[0][0]==1,'Another customer cannot append evidence')
                assess(0,0,True);check(state()==(1,'Waived',0),'Admin can close normal wear without charging a customer')
                # Customer initial evidence is part of the report transaction.
                initial_count=query('SELECT COUNT(*) FROM DamageReports')[0][0]
                customer.upload(customer_reports,{'Category':'Court','Description':'General damage noticed'},b'invalid',file_field='Evidence')
                check(query('SELECT COUNT(*) FROM DamageReports')[0][0]==initial_count,'Invalid initial evidence leaves no partial report')
                customer.upload(customer_reports,{'Category':'Court','Description':'General damage noticed'},PNG,file_field='Evidence')
                general=query('SELECT MAX(Id) FROM DamageReports')[0][0]
                admin.post(f'/Damage/Assess/{general}',{'Revision':0,'Amount':10,'Reason':'Attempt to charge unlinked report'},token_page=reports)
                check(not query('SELECT * FROM DamageClaims WHERE ReportId=?',(general,)),'General reports without booking cannot create charges')
                stop();start()
                check(query('SELECT SnapshotJson FROM DamageReceipts')[0][0]==saved and query('PRAGMA foreign_key_check')==[],'Restart preserves evidence, claims, receipt and relationships')
                check(query('SELECT COUNT(*) FROM DamageClaimEvents')[0][0]>=10,'Assessment and customer decisions retain an audit history')
                print('ALL DAMAGE CHECKS PASSED')
            except Exception:
                log.flush();print((Path(directory)/'server.log').read_text(encoding='utf-8')[-8000:]);raise
            finally:stop()


if __name__=='__main__':run()
