"""Wallet-only checkout, exact shortfalls, private continuations and legacy-proof compatibility."""
from contextlib import closing
from pathlib import Path
import datetime as dt
import html
import os
import re
import sqlite3
import subprocess
import tempfile
import time
from smoke import Browser, PASSWORD, ROOT, DLL, TODAY, check, legacy_payment
from product_smoke import port
from damage_smoke import PNG


def run():
    with tempfile.TemporaryDirectory(prefix='courtbook-wallet-only-') as directory:
        database=Path(directory)/'test.db';base=f'http://127.0.0.1:{port()}';admin_base=f'http://127.0.0.1:{port()}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Testing','Database__Provider':'Sqlite','Database__Path':str(database),
             'Portals__AdminPort':admin_base.rsplit(':',1)[1],'Demo__AutomaticTopUps':'true','Logging__LogLevel__Default':'Warning'}
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
                start();admin=Browser(admin_base);admin.login('admin@courtbook.local','CourtBook123!');member=Browser(base);other=Browser(base)
                for browser,email in [(member,'only-wallet@example.com'),(other,'other-only-wallet@example.com')]:
                    browser.post('/Account/Register',{'Name':email,'Email':email,'Password':PASSWORD,'ConfirmPassword':PASSWORD});browser.login(email)
                user=sql("SELECT Id FROM Users WHERE Email='only-wallet@example.com'")[0][0]
                def book(hour):
                    response=member.post('/Bookings/Create',{'CourtId':1,'Date':str(TODAY+dt.timedelta(days=5)),'Hours':[hour]});return int(response[1].rsplit('/',1)[1])
                booking=book(10);result=f'/Bookings/Result/{booking}';destination=f'/Wallet?bookingId={booking}'
                due=sql('SELECT NetSatang FROM Bookings WHERE Id=?',(booking,))[0][0]
                page=member.request(result)[2]
                check('wallet-payment' in page and 'payment-qr.jpg' not in page and 'SubmitPayment' not in page,'New bookings offer only wallet payment, with no direct transfer form')
                check(f'/Wallet?bookingId={booking}' in page,'Booking points to its own shortfall top-up')
                check(member.upload(f'/Bookings/SubmitPayment/{booking}',{},PNG,file_field='Slip',token_page=result)[0]==404,'Retired booking transfer endpoint cannot accept new slips')
                admin.post(f'/Admin/ConfirmBooking/{booking}',{},token_page=f'/Admin/Booking/{booking}')
                admin.post(f'/Admin/ConfirmPayment/{booking}',{'Reference':'MANUAL-BYPASS'},token_page=f'/Admin/Booking/{booking}')
                check(sql('SELECT PaidAt FROM Bookings WHERE Id=?',(booking,))[0][0] is None and 'action="/Admin/ConfirmPayment/' not in admin.request(f'/Admin/Booking/{booking}')[2],'Admin cannot bypass wallet payment for a new booking')
                sql('INSERT INTO WalletEntries(UserId,AmountSatang,Description,CreatedAt) VALUES(?,?,?,?)',(user,due-1,'Existing balance fixture',dt.datetime.now(dt.timezone.utc).isoformat()))
                page=member.request(destination+'&amount=999999&returnUrl=https://evil.example')[2]
                check('ขาดอีก ฿0.01' in html.unescape(page) and re.search(r'id="demo-amount"[^>]*value="1',page) is not None,'Shortfall uses server balance and explains the one-baht demo minimum')
                check('id="topup-slip"' not in page,'Removed manual transfer form stays absent')
                check(other.request(destination)[0]==404 and member.request(destination+'&planId=monthly')[0]==400,'Checkout continuation validates ownership and accepts only one destination')
                check(member.request('/Wallet?damageId=999999')[0]==404,'Unknown repair charge cannot become a checkout destination')
                before=sql('SELECT COUNT(*) FROM TopUps')[0][0]
                other.upload(f'/Wallet/TopUp?bookingId={booking}',{'Amount':1},PNG,file_field='Slip',token_page='/Wallet')
                check(sql('SELECT COUNT(*) FROM TopUps')[0][0]==before,'Another customer cannot submit a top-up against a private booking destination')
                member.upload(f'/Wallet/TopUp?bookingId={booking}',{'Amount':'0.01','CheckoutPath':'https://evil.example'},PNG,file_field='Slip',token_page=destination)
                topup=sql('SELECT Id FROM TopUps WHERE UserId=?',(user,))[0][0]
                check(sql('SELECT AmountSatang,Status,CheckoutPath FROM TopUps WHERE Id=?',(topup,))[0]==(1,'Pending',result),'Exact shortfall is saved with a server-generated continuation, ignoring forged paths')
                check(sql('SELECT PaidAt FROM Bookings WHERE Id=?',(booking,))[0][0] is None and sql('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==due-1,'Submitting a slip neither credits funds nor pays the booking')
                check('TU'+str(topup).zfill(6) in member.request('/Wallet')[2] and result in member.request('/Wallet')[2],'Pending top-ups and their original destination survive leaving the checkout page')
                for _ in range(2):admin.post(f'/Admin/ReviewTopUp/{topup}',{'approve':'true'},token_page='/Admin/TopUps')
                check(sql('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==due,'Approval credits the shortfall exactly once')
                check('PayWallet' in member.request(result)[2],'Approved top-up returns the customer to an actionable wallet payment')
                for _ in range(2):member.post(f'/Bookings/PayWallet/{booking}',{},token_page=result)
                check(sql('SELECT SUM(AmountSatang) FROM WalletEntries WHERE UserId=?',(user,))[0][0]==0 and len(sql('SELECT Id FROM PaymentReceipts WHERE BookingId=?',(booking,)))==1,'Customer confirmation charges once and issues one receipt')
                check(other.request(f'/Receipts/Details/RC-BK-{booking:06d}')[0]==404,'Wallet checkout receipt remains private')
                # Old proofs remain verifiable but cannot be submitted through the retired endpoint.
                old=book(11);legacy_payment(database,old,PNG+b'legacy','LEGACY-ONLY')
                check('wallet-payment' not in member.request(f'/Bookings/Result/{old}')[2] and 'PayWallet' not in member.request(f'/Bookings/Result/{old}')[2],'Pending legacy proofs do not offer a second wallet charge')
                admin.post(f'/Admin/ConfirmBooking/{old}',{},token_page=f'/Admin/Booking/{old}')
                admin.post(f'/Admin/ConfirmPayment/{old}',{'Reference':'VERIFIED-LEGACY'},token_page=f'/Admin/Booking/{old}')
                check(sql('SELECT PaidAt FROM Bookings WHERE Id=?',(old,))[0][0] is not None,'Admin can finish reviewing a genuine pre-wallet transfer')
                admin.post('/Admin/Plans',{'Id':'monthly','Price':100,'DiscountPercent':10,'IsActive':'true'})
                check('planId=monthly' in member.request('/Home/Membership')[2],'Membership shortfalls link to a plan-specific top-up')
                page=member.request('/Wallet?planId=monthly')[2]
                check(re.search(r'id="demo-amount"[^>]*value="100',page) is not None,'Plan demo top-up uses the current price and actual balance')
                response=member.post('/Wallet/StartDemoPayment?planId=monthly',{'Amount':100},token_page='/Wallet?planId=monthly')
                demo=html.unescape(re.search(r'action="([^"]*CompleteDemoPayment[^"]*)"',response[2])[1])
                check('planId=monthly' in demo,'Demo checkout preserves the original destination without charging it')
                member.post(demo,{'outcome':'Paid'},token_page=response[1]+'?planId=monthly')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==0,'Top-up completion never automatically purchases a plan')
                member.post('/Wallet/Buy',{'planId':'monthly','expectedPriceSatang':10000,'expectedDiscountPercent':10,'expectedMonths':1},token_page='/Home/Membership')
                check(sql('SELECT COUNT(*) FROM Memberships')[0][0]==1,'Member confirms purchase after returning from top-up')
                admin.upload(f'/Admin/ReportDamage/{booking}',{'Category':'Court','Description':'Damage checkout fixture'},PNG,file_field='Evidence')
                report=sql('SELECT MAX(Id) FROM DamageReports')[0][0]
                admin.post(f'/Damage/Assess/{report}',{'Revision':0,'Amount':20,'Reason':'Documented material repair cost'},token_page='/Admin/DamageReports')
                member.post(f'/Damage/Respond/{report}',{'Revision':1,'Decision':'Accept'},token_page='/Bookings/ReportDamage')
                check(f'damageId={report}' in member.request('/Bookings/ReportDamage')[2],'Accepted repair charge links to its own shortfall top-up')
                check(re.search(r'id="demo-amount"[^>]*value="20',member.request(f'/Wallet?damageId={report}')[2]) is not None,'Repair shortfall uses the accepted current assessment')
                stop();start()
                check(sql('SELECT CheckoutPath FROM TopUps WHERE Id=?',(topup,))[0][0]==result and sql('PRAGMA foreign_key_check')==[],'Restart preserves top-up destinations and valid relationships')
                # Reconstruct the previous minimum and missing checkout column in this disposable fixture.
                stop()
                sql('UPDATE TopUps SET AmountSatang=100 WHERE Id=?',(topup,))
                sql('UPDATE WalletEntries SET AmountSatang=100 WHERE TopUpId=?',(topup,))
                columns='Id,UserId,AmountSatang,TransferReference,SlipData,SlipContentType,SlipHash,Status,CreatedAt,ReviewedAt,ReviewedBy,ReviewNote'
                expected_topups=sql('SELECT '+columns+' FROM TopUps ORDER BY Id')
                expected_ledger=sql('SELECT * FROM WalletEntries ORDER BY Id')
                with closing(sqlite3.connect(database)) as c:
                    c.executescript("""CREATE TABLE TopUps_old(Id INTEGER PRIMARY KEY,UserId INTEGER NOT NULL REFERENCES Users(Id),AmountSatang INTEGER NOT NULL CHECK(AmountSatang BETWEEN 100 AND 10000000),TransferReference TEXT COLLATE NOCASE NOT NULL UNIQUE,SlipData BLOB NOT NULL,SlipContentType TEXT NOT NULL,SlipHash TEXT NOT NULL UNIQUE,Status TEXT NOT NULL DEFAULT 'Pending' CHECK(Status IN ('Pending','Approved','Rejected')),CreatedAt TEXT NOT NULL,ReviewedAt TEXT,ReviewedBy INTEGER REFERENCES Users(Id),ReviewNote TEXT NOT NULL DEFAULT '');""")
                    c.execute(f'INSERT INTO TopUps_old({columns}) SELECT {columns} FROM TopUps')
                    c.executescript('DROP TABLE TopUps; ALTER TABLE TopUps_old RENAME TO TopUps;');c.commit()
                start()
                check(sql('SELECT '+columns+' FROM TopUps ORDER BY Id')==expected_topups and sql('SELECT * FROM WalletEntries ORDER BY Id')==expected_ledger and sql('PRAGMA foreign_key_check')==[],'Old top-up schema upgrades without losing IDs, approvals, proofs or linked wallet entries')
                member=Browser(base);member.login('only-wallet@example.com')
                member.upload('/Wallet/TopUp',{'Amount':'0.01'},PNG+b'after-migration',file_field='Slip',token_page='/Wallet')
                check(sql('SELECT AmountSatang FROM TopUps ORDER BY Id DESC LIMIT 1')[0][0]==1,'Migrated databases accept exact one-satang top-ups')
                print('ALL WALLET-ONLY CHECKS PASSED',flush=True)
            except Exception:
                log.flush();print((Path(directory)/'server.log').read_text(encoding='utf-8')[-8000:]);raise
            finally:stop()


if __name__=='__main__':run()
