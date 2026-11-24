import {spawnSync} from 'node:child_process';
import {writeFile} from 'node:fs/promises';
import path from 'node:path';

export async function runCheckInScenario({call,evaluate,waitFor,waitPath,navigate,fill,submit,screenshot,base,adminBase,temp,output,setBase}){
  const password='Entry-browser-123!';
  await navigate('/Account/Register');await fill({Name:'สมาชิกเช็กอินทดสอบ',Email:'entry-browser@example.com',Password:password,ConfirmPassword:password});await submit('/Account/Login');
  await fill({Email:'entry-browser@example.com',Password:password});await submit('/Courts');
  await navigate('/Bookings/Create?courtId=1');
  await evaluate("(() => {const input=document.querySelector('[name=Hours]:not(:disabled)');input.checked=true;input.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await waitFor("!document.getElementById('confirm-booking').disabled");await evaluate("document.getElementById('booking-form').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Bookings/Result/')&&document.readyState==='complete'");
  const id=await evaluate("Number(location.pathname.split('/').at(-1))");
  if(await evaluate("document.querySelector('a[href*=EntryPass]')!==null"))throw new Error('Pending booking exposes admission pass');
  await navigate('/Wallet');await fill({Amount:'1000'});await evaluate("document.querySelector('form[action*=StartDemoPayment]').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Wallet/DemoPayment/')&&document.readyState==='complete'");
  await evaluate("document.querySelector('button[name=outcome][value=Paid]').click()");await waitPath('/Wallet');
  await navigate('/Bookings/Result/'+id);await evaluate("window.confirm=()=>true;document.querySelector('form[action*=PayWallet]').requestSubmit()");
  await waitFor("document.querySelector('form[action*=PayWallet]')===null");
  setBase(adminBase);await navigate('/Account/Login');await fill({Email:'admin@courtbook.local',Password:'CourtBook123!'});await submit('/Admin');
  await navigate('/Admin/Booking/'+id);await evaluate("document.querySelector('form[action*=ConfirmBooking]').requestSubmit()");
  await waitFor("document.querySelector('form[action*=ConfirmBooking]')===null");
  const fixture=spawnSync('python',['-c',"import sqlite3,datetime,sys; n=datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(hours=7); c=sqlite3.connect(sys.argv[1]); c.execute('UPDATE Bookings SET PlayDate=?,StartHour=?,Hours=1 WHERE Id=?',(str(n.date()),n.hour,int(sys.argv[2]))); c.commit()",path.join(temp,'courtbook.db'),String(id)],{windowsHide:true,encoding:'utf8'});
  if(fixture.status!==0)throw new Error('Live entry fixture failed: '+fixture.stderr);
  for(const width of [360,768,1440]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:950,deviceScaleFactor:1,mobile:width<680});
    for(const portal of [base,adminBase]){
      setBase(portal);await navigate((portal===base?'/Receipts/Details/':'/Admin/Receipt/')+'RC-BK-'+String(id).padStart(6,'0'));
      await screenshot('07-receipt-'+(portal===base?'customer':'admin')+'-'+width);
    }
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  setBase(base);await navigate('/Bookings/EntryPass/'+id);
  await waitFor("document.getElementById('entry-qr')?.naturalWidth>0");await screenshot('01-customer-entry-pass');
  const png=await evaluate("fetch(document.getElementById('entry-qr').src).then(r=>r.arrayBuffer()).then(b=>{let s='';for(const n of new Uint8Array(b))s+=String.fromCharCode(n);return btoa(s);})");
  const qrFile=path.join(temp,'actual-entry-qr.png');await writeFile(qrFile,Buffer.from(png,'base64'));
  setBase(adminBase);await navigate('/Admin/CheckIn');await screenshot('02-admin-scanner');
  if(!await evaluate("typeof Html5Qrcode==='function'"))throw new Error('Local QR decoder did not load');
  const document=await call('DOM.getDocument');const input=await call('DOM.querySelector',{nodeId:document.root.nodeId,selector:'#checkin-qr-file'});
  await call('DOM.setFileInputFiles',{nodeId:input.nodeId,files:[qrFile]});
  await waitPath('/Admin/CheckInLookup');await waitFor("document.getElementById('confirm-checkin-form')!==null");
  if(!await evaluate("document.getElementById('checkin-result').innerText.includes('สมาชิกเช็กอินทดสอบ')"))throw new Error('Decoded QR did not match actual booking owner');
  if(await evaluate("document.getElementById('confirm-checkin-form').checkValidity()"))throw new Error('Identity verification checkbox is not required');
  await screenshot('03-scanned-preview');
  await evaluate("document.querySelector('#confirm-checkin-form [name=IdentityVerified]').checked=true;document.getElementById('confirm-checkin-form').requestSubmit()");
  await waitPath('/Admin/CheckIn/'+id);
  await waitFor("document.getElementById('checkin-result').innerText.includes('เช็กอินแล้ว')");
  if(await evaluate("document.getElementById('confirm-checkin-form')!==null"))throw new Error('Checked-in booking offers another confirmation');
  await screenshot('04-admin-checked-in');
  await evaluate("document.getElementById('start-checkin-camera').click()");
  await waitFor("document.getElementById('checkin-scan-status').innerText.includes('เปิดกล้องไม่ได้')");
  const reference='CB'+String(id).padStart(6,'0');await fill({Code:reference});await evaluate("document.getElementById('checkin-lookup-form').requestSubmit()");
  await waitPath('/Admin/CheckInLookup');
  if(!await evaluate("document.getElementById('checkin-result').innerText.includes('เช็กอินแล้ว')"))throw new Error('Manual reference lookup cannot see previous admission');
  for(const width of [360,768]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:950,deviceScaleFactor:1,mobile:width<680});
    await navigate('/Admin/CheckIn?id='+id);
    if(await evaluate('document.documentElement.scrollWidth>innerWidth+1'))throw new Error('Admin check-in overflow at '+width);
    await screenshot('05-admin-checkin-'+width);
    setBase(base);await navigate('/Bookings/EntryPass/'+id);
    if(await evaluate('document.documentElement.scrollWidth>innerWidth+1'))throw new Error('Customer pass overflow at '+width);
    if(!await evaluate("document.querySelector('.entry-pass').innerText.includes('เช็กอินแล้ว')"))throw new Error('Customer pass does not show live check-in status');
    await screenshot('06-customer-checked-in-'+width);setBase(adminBase);
  }
  setBase(adminBase);await navigate('/Admin/Booking/'+id);
  if(await evaluate("document.querySelector('form[action*=ConfirmCheckOut]').checkValidity()"))throw new Error('Checkout must require staff verification');
  for(const width of [360,768,1440]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:1050,deviceScaleFactor:1,mobile:width<680});
    if(await evaluate('document.documentElement.scrollWidth>innerWidth+1'))throw new Error('Checkout layout overflow at '+width);
    await screenshot('08-admin-checkout-'+width);
  }
  await evaluate("const f=document.querySelector('form[action*=ConfirmCheckOut]');f.querySelector('[name=EquipmentVerified]').checked=true;f.requestSubmit()");
  await waitFor("document.querySelector('form[action*=ConfirmCheckOut]')===null");
  await screenshot('09-admin-checked-out');
  setBase(base);await navigate('/Bookings/Result/'+id);
  if(!await evaluate("document.querySelector('main').innerText.includes('เช็กเอาต์แล้ว')&&!document.querySelector('a[href*=EntryPass]')"))throw new Error('Customer checkout status or admission visibility is wrong');
  await screenshot('10-customer-checked-out');
  await writeFile(path.join(output,'checkin-verification.json'),JSON.stringify({decodedActualQr:true,explicitStaffConfirmation:true,duplicateCheckInBlocked:true,cameraFailureFallback:true,staffCheckout:true,mobileWidths:[360,768],productionDatabaseChanged:false},null,2));
  console.log('PASS: Generated booking QR decoded locally, staff confirmed identity, live check-in shown, camera fallback and responsive layouts passed.');
}
