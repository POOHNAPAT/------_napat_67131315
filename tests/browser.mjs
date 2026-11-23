// Capture real application pages using a temporary database and a local test host.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, mkdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import net from 'node:net';

const root = process.cwd();
const temp = await mkdtemp(path.join(tmpdir(), 'courtbook-browser-'));
const output = process.env.COURTBOOK_SCREENSHOTS || path.join(root, 'docs', 'screenshots');
await mkdir(output, { recursive: true });
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const port = await new Promise(resolve => {
  const server = net.createServer();
  server.listen(0, '127.0.0.1', () => {
    const port = server.address().port;
    server.close(() => resolve(port));
  });
});
const base = `http://127.0.0.1:${port}`;
const adminPort = await new Promise(resolve => {
  const server = net.createServer();
  server.listen(0, '127.0.0.1', () => {
    const value = server.address().port;
    server.close(() => resolve(value));
  });
});
const adminBase = `http://127.0.0.1:${adminPort}`;
const secondCustomerPort = await new Promise(resolve => {
  const server = net.createServer();
  server.listen(0, '127.0.0.1', () => {
    const value = server.address().port;
    server.close(() => resolve(value));
  });
});
const secondCustomerBase = `http://127.0.0.1:${secondCustomerPort}`;
let activeBase = base;
let server, browser, ws;
let log = '';
try {
  server = spawn('dotnet', [process.env.COURTBOOK_TEST_DLL || path.join(root, 'bin/Debug/net10.0/CourtBook.dll'), '--urls', [base,adminBase,secondCustomerBase].join(';')], {
    cwd: root, windowsHide: true,
    env: { ...process.env, Database__Path: path.join(temp, 'courtbook.db'), Portals__AdminPort: String(adminPort), ASPNETCORE_ENVIRONMENT: 'Testing', Logging__LogLevel__Default: 'Warning' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  for (const stream of [server.stdout, server.stderr]) stream.on('data', data => { log = (log + data).slice(-12000); });
  let ready = false;
  for (let i = 0; i < 100; i++) {
    if (server.exitCode !== null) throw new Error(`Report host stopped: ${log}`);
    try { ready = (await fetch(base + '/Account/Login')).ok; } catch {}
    if (ready) break;
    await pause(100);
  }
  if (!ready) throw new Error(`Report host not ready: ${log}`);
  const profile = path.join(temp, 'chrome');
  browser = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
    '--headless', '--no-sandbox', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank',
  ], { windowsHide: true, stdio: 'ignore' });
  let debugPort;
  for (let i = 0; i < 100; i++) {
    try { debugPort = (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; } catch {}
    if (debugPort) break;
    await pause(100);
  }
  if (!debugPort) throw new Error('Browser did not start');
  const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
  ws = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  const clientErrors = [];
  let id = 0;
  const pending = new Map();
  ws.onmessage = event => {
    const data = JSON.parse(event.data);
    if(data.method === 'Runtime.exceptionThrown') clientErrors.push(data.params.exceptionDetails.text);
    if(data.method === 'Network.responseReceived' && data.params.response.status >= 500 && !data.params.response.url.endsWith('/Home/Error')) clientErrors.push(data.params.response.url);
    if (pending.has(data.id)) {
      const { resolve, reject, timer } = pending.get(data.id);
      pending.delete(data.id); clearTimeout(timer);
      data.error ? reject(new Error(JSON.stringify(data.error))) : resolve(data.result);
    }
  };
  function call(method, params = {}) {
    return new Promise((resolve, reject) => {
      const requestId = ++id;
      const timer = setTimeout(() => { pending.delete(requestId); reject(new Error(`Timeout: ${method}`)); }, 10000);
      pending.set(requestId, { resolve, reject, timer });
      ws.send(JSON.stringify({ id: requestId, method, params }));
    });
  }
  async function evaluate(expression) {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  }
  async function waitPath(expected) {
    for (let i = 0; i < 100; i++) {
      try {
        if (await evaluate(`(location.pathname === ${JSON.stringify(expected)} || (${JSON.stringify(expected)} === '/Account/Login' && location.pathname === '/')) && document.readyState === 'complete'`)) return;
      } catch {}
      await pause(100);
    }
    throw new Error(`Expected ${expected}, got ${await evaluate('location.href')}`);
  }
  async function navigate(route) {
    await call('Page.navigate', { url: activeBase + route });
    await waitPath(new URL(route, base).pathname);
  }
  async function fill(values) {
    await evaluate(`(() => { const fields=${JSON.stringify(values)}; for (const [name,value] of Object.entries(fields)) document.querySelector('[name="'+name+'"]').value=value; })()`);
  }
  async function submit(next) {
    await evaluate(`Array.from(document.querySelectorAll('main form')).find(f => f.method === 'post').requestSubmit()`);
    await waitPath(next);
  }
  async function waitFor(expression) {
    for(let i=0;i<100;i++) {
      try { if(await evaluate(expression)) return; } catch {}
      await pause(100);
    }
    throw new Error('Timed out waiting for: ' + expression);
  }
  await call('Page.enable');
  await call('Runtime.enable');
  await call('Network.enable');
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // Capture the whole 1440px canvas, including long forms.
  async function screenshot(name) {
    await evaluate('document.fonts.ready.then(() => true)');
    const width = await evaluate('innerWidth');
    const height = Math.ceil(await evaluate("document.querySelector('footer').getBoundingClientRect().bottom + 16"));
    const result = await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width,height,scale:1}});
    await writeFile(path.join(output,name+'.png'),Buffer.from(result.data,'base64'));
  }
  const password='Browser-test-123!';
  await navigate('/');
  if(await evaluate("document.querySelector('main').innerText.includes('@category')")) throw new Error('Unrendered Razor expression in sport categories');
  if(!await evaluate("document.querySelectorAll('.sport-category-card').length===3")) throw new Error('Three sports not shown');
  await screenshot('01-home');
  // Use a screenshot of the app's court illustration as an isolated upload fixture.
  const uploadFixture=path.join(temp,'court-upload.png');
  const photoClip=await evaluate("(() => {const r=document.querySelector('.court-visual').getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,scale:1};})()");
  const fixtureImage=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:photoClip});
  await writeFile(uploadFixture,Buffer.from(fixtureImage.data,'base64'));
  if(!await evaluate("document.querySelector('.nav-main a[aria-current=page]')?.pathname==='/'")) throw new Error('Home navigation is not marked current');
  await fill({courtId:'2'});
  await evaluate("document.querySelector('.quick-book').requestSubmit()");
  await waitFor("location.pathname==='/Courts' && new URLSearchParams(location.search).get('courtId')==='2' && document.readyState==='complete'");
  if(!await evaluate("document.querySelector('.nav-main a[aria-current=page]')?.pathname==='/Courts'")) throw new Error('Court navigation is not marked current');
  await navigate('/Courts'); await screenshot('02-courts');
  await navigate('/Account/Register');
  await fill({Name:'สมาชิกทดสอบหน้าจอ',Email:'browser@example.com',Password:password,ConfirmPassword:password});
  await screenshot('03-register'); await submit('/Account/Login');
  await fill({Email:'browser@example.com',Password:password}); await screenshot('04-login'); await submit('/Courts');
  activeBase=secondCustomerBase;
  await call('Page.navigate',{url:secondCustomerBase+'/Account/Profile'});
  await waitPath('/Account/Login');
  await navigate('/Account/Register');
  await fill({Name:'สมาชิกช่องที่สอง',Email:'browser-second@example.com',Password:password,ConfirmPassword:password});
  await submit('/Account/Login');
  await fill({Email:'browser-second@example.com',Password:password}); await submit('/Courts');
  await navigate('/Account/Profile');
  if(!await evaluate("document.querySelector('main').innerText.includes('browser-second@example.com')")) throw new Error('Second customer port has wrong account');
  activeBase=base;
  await navigate('/Account/Profile');
  if(!await evaluate("document.querySelector('main').innerText.includes('browser@example.com')")) throw new Error('Second customer login replaced first account');
  activeBase=secondCustomerBase;
  await navigate('/Account/Profile');
  await evaluate("document.querySelector('header form').requestSubmit()"); await waitPath('/');
  activeBase=base;
  await navigate('/Account/Profile');
  if(!await evaluate("document.querySelector('main').innerText.includes('browser@example.com')")) throw new Error('Second customer logout signed out first account');
  await navigate('/Wallet'); await screenshot('31-wallet');
  if(!await evaluate("document.querySelector('form[action=\"/Wallet/TopUp\"]') !== null")) throw new Error('Wallet top-up form missing');
  await navigate('/Bookings/Create?courtId=1');
  await evaluate("(() => {const f=document.getElementById('booking-form');const first=f.querySelector('[name=Hours][value=\"8\"]');first.checked=true;const elapsed=Date.parse(first.dataset.slotStart)-Number(f.dataset.serverNow)+performance.now()+2000;Object.defineProperty(performance,'now',{configurable:true,value:()=>elapsed});window.dispatchEvent(new Event('focus'));})()");
  await waitFor("document.querySelector('[name=Hours][value=\"8\"]').disabled && !document.querySelector('[name=Hours][value=\"8\"]').checked");
  await navigate('/Bookings/Create?courtId=1');
  await evaluate(`(() => { const f=document.getElementById('booking-form'); for(const h of [9,10,11]) f.querySelector('[name=Hours][value="'+h+'"]').checked=true; f.querySelector('[name=PromotionId]').value='2'; f.dispatchEvent(new Event('change',{bubbles:true})); })()`);
  await waitFor("document.getElementById('net').innerText.includes('510.00') && !document.getElementById('confirm-booking').disabled");
  await evaluate(`(() => { document.getElementById('equipment-racket').value='2'; document.getElementById('equipment-shoes').value='1'; document.getElementById('booking-form').dispatchEvent(new Event('change',{bubbles:true})); })()`);
  await waitFor("document.getElementById('net').innerText.includes('640.00') && document.getElementById('equipment-total').innerText.includes('130.00') && !document.getElementById('confirm-booking').disabled");
  await screenshot('05a-equipment-rental');
  await call('Emulation.setDeviceMetricsOverride', {width:390,height:844,deviceScaleFactor:1,mobile:true});
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Equipment form overflows on mobile');
  await screenshot('05b-equipment-mobile');
  await call('Emulation.clearDeviceMetricsOverride');
  await evaluate(`(() => { document.getElementById('equipment-racket').value='0'; document.getElementById('equipment-shoes').value='0'; document.getElementById('booking-form').dispatchEvent(new Event('change',{bubbles:true})); })()`);
  await waitFor("document.getElementById('net').innerText.includes('510.00') && !document.getElementById('confirm-booking').disabled");
  await evaluate("document.getElementById('start-hour').value='9'; document.getElementById('end-hour').value='12'; document.getElementById('apply-time-range').click()");
  await waitFor("document.getElementById('selected-time').innerText.includes('09:00') && document.getElementById('net').innerText.includes('510.00') && !document.getElementById('confirm-booking').disabled");
  await screenshot('05-booking');
  await evaluate("document.getElementById('booking-form').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Bookings/Result/') && document.readyState==='complete'");
  await screenshot('06-result');
  await waitFor("document.querySelector('.qr-code-card img')?.naturalWidth>0");
  if(!await evaluate("document.querySelector('#qr-payment').getBoundingClientRect().top<document.querySelector('.booking-details').getBoundingClientRect().top")) throw new Error('Pending booking QR is hidden below details');
  await screenshot('30-qr-immediately-after-booking');
  await navigate('/Bookings/History');
  if(!await evaluate("Array.from(document.querySelectorAll('main a')).some(a=>a.hash==='#qr-payment')")) throw new Error('Booking history has no direct QR payment link');
  await navigate('/Bookings/Create?courtId=2&hour=13');
  await waitFor("!document.getElementById('confirm-booking').disabled");
  await evaluate("document.getElementById('booking-form').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Bookings/Result/') && document.readyState==='complete'");
  await evaluate("document.querySelector('main details').open=true");
  await screenshot('19-cancel-booking');
  await evaluate("document.querySelector('main form[action*=Cancel]').requestSubmit()");
  await waitFor("document.querySelector('main h1').innerText.includes('ยกเลิก') && !document.querySelector('main form[action*=Cancel]')");
  await screenshot('20-cancelled-booking');
  await navigate('/Bookings/History'); await screenshot('07-history');
  await navigate('/Home/Promotions'); await screenshot('08-promotions');
  // Keep the customer session while signing into the separate admin portal.
  activeBase = adminBase;
  await navigate('/Account/Login'); await fill({Email:'admin@courtbook.local',Password:'CourtBook123!'}); await submit('/Admin');
  await screenshot('09-admin');
  await navigate('/Admin/Plans'); await screenshot('32-membership-plans');
  if(await evaluate("document.querySelector('main').innerText.includes('@plan')")) throw new Error('Plan button contains unrendered Razor expression');
  await navigate('/Admin/TopUps'); await screenshot('33-admin-topups');
  await navigate('/Admin/Courts'); await screenshot('10-admin-courts');
  await navigate('/Admin/Court/1'); await screenshot('10b-court-form');
  const documentNode=await call('DOM.getDocument');
  const uploadInput=await call('DOM.querySelector',{nodeId:documentNode.root.nodeId,selector:'#Image'});
  await call('DOM.setFileInputFiles',{nodeId:uploadInput.nodeId,files:[uploadFixture]});
  await waitFor("document.getElementById('court-image-preview').src.startsWith('blob:') && document.getElementById('court-image-preview').naturalWidth>0");
  await screenshot('21-court-upload-preview');
  await submit('/Admin/Courts');
  await waitFor("document.querySelector('main img.court-photo')?.naturalWidth>0");
  await screenshot('22-court-photo-admin');
  activeBase=base;
  await navigate('/');
  await waitFor("document.querySelector('.hero-court-photo img')?.naturalWidth>0");
  await screenshot('23-home-with-photo');
  await navigate('/Courts?courtId=1');
  await waitFor("document.querySelector('.schedule-photo img')?.naturalWidth>0");
  await screenshot('24-court-schedule-photo');
  await navigate('/Bookings/Create?courtId=1');
  await waitFor("document.querySelector('.booking-court-photo img')?.naturalWidth>0");
  activeBase=adminBase;
  await navigate('/Admin/Schedule');
  await evaluate("document.querySelector('[name=blocked][value=\"14\"]').checked=true"); await submit('/Admin/Schedule');
  await screenshot('11-admin-schedule');
  await navigate('/Admin/Bookings'); await screenshot('12-admin-bookings');
  await navigate('/Admin/Booking/1'); await screenshot('12b-booking-detail');
  await evaluate("document.querySelector('main form[action*=ConfirmBooking]').requestSubmit()");
  await waitFor("document.querySelector('main form[action*=ConfirmPayment]') !== null");
  activeBase=base;
  await navigate('/Bookings/Result/1');
  await waitFor("document.querySelector('.qr-code-card img')?.naturalWidth>0");
  if(!await evaluate("document.querySelector('.payment-amount').innerText.includes('510.00')")) throw new Error('QR payment does not use saved discounted amount');
  await screenshot('26-qr-payment');
  await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('QR payment overflows mobile viewport');
  await screenshot('27-mobile-qr-payment');
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  if(await evaluate("!!document.querySelector('input[name=TransferReference]')")) throw new Error('Transfer reference input should be removed');
  const slipDocument=await call('DOM.getDocument');
  const slipInput=await call('DOM.querySelector',{nodeId:slipDocument.root.nodeId,selector:'#payment-slip'});
  // A test image proves uploading evidence never automatically confirms payment.
  await call('DOM.setFileInputFiles',{nodeId:slipInput.nodeId,files:[uploadFixture]});
  await evaluate("document.querySelector('form[action*=SubmitPayment]').requestSubmit()");
  await waitFor("document.querySelector('main').innerText.includes('รอตรวจสอบการชำระเงิน') && !document.querySelector('.qr-code-card')");
  await screenshot('28-payment-proof-submitted');
  activeBase=adminBase;
  await navigate('/Admin/Booking/1');
  await waitFor("document.querySelector('.payment-slip-image')?.naturalWidth>0");
  await screenshot('29-admin-payment-proof');
  await fill({Reference:'BROWSER-BANK-510'});
  await evaluate("document.querySelector('main form[action*=ConfirmPayment]').requestSubmit()");
  await waitFor("document.querySelector('main').innerText.includes('BROWSER-BANK-510') && !document.querySelector('main form[action*=ConfirmPayment]')");
  await screenshot('12c-payment-confirmed');
  for(const width of [360,768,1440]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:950,deviceScaleFactor:1,mobile:width<680});
    if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Booking management overflow at ${width}`);
    const overlap=await evaluate(`(() => {
      const boxes=[...document.querySelectorAll('.booking-action-panel .button,.booking-management-footer .button')].map(e=>e.getBoundingClientRect());
      return boxes.some((a,i)=>boxes.some((b,j)=>j>i && Math.min(a.right,b.right)>Math.max(a.left,b.left) && Math.min(a.bottom,b.bottom)>Math.max(a.top,b.top)));
    })()`);
    if(overlap) throw new Error(`Booking management actions overlap at ${width}`);
    await screenshot(`58-booking-actions-${width}`);
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  activeBase = base;
  await navigate('/Bookings/Result/1');
  if(!await evaluate("document.querySelector('main').innerText.includes('BROWSER-BANK-510')")) throw new Error('Customer cannot see payment confirmation');
  activeBase = adminBase;
  await navigate('/Admin/Customers'); await screenshot('16-customers');
  await navigate('/Admin/Customer/3'); await screenshot('17-customer-detail');
  await navigate('/Admin/SportTypes');
  await fill({Name:'Tennis'}); await submit('/Admin/SportTypes');
  await waitFor("document.querySelector('main table').innerText.includes('Tennis')");
  await screenshot('18-sport-types');
  await navigate('/Admin/Court/2'); await fill({SportTypeId:'2'}); await submit('/Admin/Courts');
  activeBase = base;
  await navigate('/Courts?courtId=2');
  if(!await evaluate("document.querySelector('main').innerText.includes('Tennis')")) throw new Error('Customer cannot see sport type');
  activeBase = adminBase;
  activeBase=base;
  await navigate('/');
  if(!await evaluate("getComputedStyle(document.body).backgroundColor === 'rgb(16, 19, 16)'")) throw new Error('Dark theme not applied');
  await evaluate("document.querySelector('.sport-filter[data-sport=\"2\"]').click()");
  if(!await evaluate("document.querySelectorAll('[data-court-sport]:not([hidden])').length === 1 && document.querySelector('[data-court-sport=\"1\"]').hidden")) throw new Error('Sport filter does not hide other sports');
  await evaluate("document.querySelector('.sport-filter[data-sport=all]').click()");
  if(!await evaluate("document.querySelectorAll('[data-court-sport]:not([hidden])').length === 2")) throw new Error('All-sports filter does not restore courts');
  await screenshot('38-dark-home-sport-filters');
  activeBase=adminBase;
  await navigate('/Admin/Equipment'); await screenshot('35-admin-equipment');
  await navigate('/Admin/EquipmentItem');
  await fill({Name:'Tennis racket',Unit:'piece',Price:'65.50',SportTypeId:'2'});
  await screenshot('36-equipment-editor');
  await submit('/Admin/Equipment');
  await waitFor("document.querySelector('main table').innerText.includes('Tennis racket')");
  activeBase=base;
  await navigate('/Bookings/Create?courtId=2&hour=15');
  if(await evaluate("document.getElementById('equipment-racket') !== null")) throw new Error('Tennis court shows badminton racket');
  await evaluate(`(() => { const input=document.querySelector('.equipment-row input'); input.value='2'; input.dispatchEvent(new Event('change',{bubbles:true})); })()`);
  await waitFor("document.getElementById('equipment-total').innerText.includes('131.00') && document.getElementById('net').innerText.includes('331.00') && !document.getElementById('confirm-booking').disabled");
  await screenshot('37-sport-specific-equipment');
  activeBase=adminBase;
  await navigate('/Admin/Promotions'); await screenshot('13-admin-promotions');
  await navigate('/Admin/Promotion/2'); await screenshot('13b-promotion-form');
  await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  activeBase = base;
  await navigate('/');
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Mobile home overflows viewport');
  if(!await evaluate("(() => {const r=document.querySelector('.nav-main').getBoundingClientRect();return Math.abs(r.bottom-innerHeight)<2 && getComputedStyle(document.querySelector('.nav-main')).position==='fixed';})()")) throw new Error('Mobile navigation is not anchored to viewport bottom');
  await screenshot('14-mobile-home');
  activeBase = adminBase;
  await navigate('/Admin/Schedule');
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Mobile admin overflows viewport');
  await screenshot('15-mobile-admin');
  await navigate('/Admin/Court/1');
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Mobile image editor overflows viewport');
  await waitFor("document.getElementById('court-image-preview').naturalWidth>0");
  await screenshot('25-mobile-image-editor');
  for(const width of [360,768]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<680});
    activeBase=base;
    for(const route of ['/','/Courts','/Wallet','/Bookings/Create?courtId=1','/Account/Login']) {
      await navigate(route);
      if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Responsive overflow at ${width}: ${route}`);
      if(route==='/Wallet') await screenshot(`34-wallet-${width}`);
    }
    activeBase=adminBase;
    await navigate('/Admin');
    if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Admin overflow at ${width}`);
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  activeBase=base;
  await navigate('/Courts/Details/1'); await screenshot('39-court-details');
  await navigate('/Home/Membership'); await screenshot('40-membership-tiers');
  if(!await evaluate("document.querySelectorAll('.membership-card').length === 2")) throw new Error('Monthly and yearly plans not shown');
  await navigate('/Account/EditProfile'); await screenshot('42-edit-profile');
  await navigate('/Home/About'); await screenshot('43-about');
  activeBase=adminBase;
  const reportDate=new Date(Date.now()+7*3600000+86400000).toISOString().slice(0,10);
  await navigate('/Admin/Revenue?from='+reportDate+'&to='+reportDate); await screenshot('41-admin-revenue');
  for(const width of [360,768]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<680});
    activeBase=base;
    for(const route of ['/Home/Membership','/Courts/Details/1','/Account/EditProfile','/Home/About']) {
      await navigate(route);
      if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Product page overflow: ${route} at ${width}`);
    }
    activeBase=adminBase;await navigate('/Admin/Revenue');
    if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Revenue overflow at ${width}`);
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  activeBase=base;
  await navigate('/Bookings/Create?courtId=1&hour=20');
  await evaluate("document.getElementById('equipment-racket').value='1'; document.getElementById('booking-form').dispatchEvent(new Event('change',{bubbles:true}))");
  await waitFor("!document.getElementById('confirm-booking').disabled");
  await evaluate("document.getElementById('booking-form').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Bookings/Result/') && document.readyState==='complete'");
  const damagedBooking=await evaluate("location.pathname.split('/').at(-1)");
  const reportPath='/Bookings/ReportDamage/'+damagedBooking;
  await navigate(reportPath);
  await fill({Category:'Equipment'});
  await evaluate("document.getElementById('damage-category').dispatchEvent(new Event('change',{bubbles:true}))");
  if(!await evaluate("!document.getElementById('damage-equipment-field').hidden && document.getElementById('damage-equipment').required")) throw new Error('Equipment report selector did not activate');
  await fill({EquipmentId:'racket',Description:'Racket grip is damaged after use'});
  await submit(reportPath);
  await waitFor("document.querySelector('.damage-report-record') !== null");
  await screenshot('44-customer-damage-report');
  activeBase=adminBase;await navigate('/Admin/DamageReports');
  await fill({Status:'InProgress',AdminNote:'Maintenance team is checking the racket'});
  await submit('/Admin/DamageReports');
  await waitFor("document.querySelector('.damage-status-InProgress') !== null");
  await screenshot('45-admin-damage-reports');
  activeBase=base;await navigate(reportPath);
  if(!await evaluate("document.querySelector('.damage-admin-reply').innerText.includes('Maintenance team is checking the racket')")) throw new Error('Customer cannot see admin damage reply');
  await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Damage report overflows on mobile');
  await screenshot('46-damage-report-mobile');
  activeBase=adminBase;await navigate('/Admin/DamageReports');
  if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Admin damage report overflows on mobile');
  activeBase=base;
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await navigate('/Bookings/ReportDamage');
  await fill({Category:'Other',Subject:'Cannot load booking page',Description:'The booking page did not open when I tried to use the website'});
  await submit('/Bookings/ReportDamage');
  await waitFor("document.querySelector('main').innerText.includes('Cannot load booking page')");
  await screenshot('47-direct-customer-report');
  activeBase=adminBase;await navigate('/Admin/DamageReports?status=Reported');
  await fill({Status:'Resolved',AdminNote:'Direct issue has been checked'});
  await submit('/Admin/DamageReports');
  activeBase=base;await navigate('/Bookings/ReportDamage');
  if(!await evaluate("document.querySelector('main').innerText.includes('Direct issue has been checked')")) throw new Error('Direct report reply is missing');
  // This route intentionally returns HTTP 500 while showing a usable recovery form.
  await navigate('/Home/Error');await screenshot('48-error-page-message-form');
  await fill({Category:'Equipment',Subject:'Shared training equipment',Description:'Training equipment is damaged and needs checking'});
  await submit('/Bookings/ReportDamage');
  await waitFor("document.querySelector('main').innerText.includes('Training equipment is damaged and needs checking')");
  for(const width of [360,768]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<680});
    await navigate('/Bookings/ReportDamage');
    if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Direct reporting overflow at ${width}`);
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  activeBase=base;await navigate('/Receipts/Details/RC-BK-000001');
  if(!await evaluate("document.querySelector('.receipt-totals').innerText.includes('510.00') && document.querySelector('.entry-status').dataset.bookingStatus==='Confirmed'")) throw new Error('Booking receipt amount or admission state is wrong');
  await screenshot('49-booking-receipt');
  await call('Emulation.setEmulatedMedia',{media:'print'});
  if(!await evaluate("getComputedStyle(document.querySelector('.site-header')).display==='none' && getComputedStyle(document.querySelector('.receipt-document')).backgroundColor==='rgb(255, 255, 255)'")) throw new Error('Receipt print stylesheet is missing');
  const receiptPdf=await call('Page.printToPDF',{printBackground:false,preferCSSPageSize:true});
  await writeFile(path.join(output,'booking-receipt.pdf'),Buffer.from(receiptPdf.data,'base64'));
  await call('Emulation.setEmulatedMedia',{media:'screen'});
  activeBase=adminBase;await navigate('/Admin/Plans');
  await evaluate("(() => {const f=document.querySelector('input[name=Id][value=monthly]').form; f.querySelector('[name=Price]').value='100';f.querySelector('[name=DiscountPercent]').value='10';f.querySelector('[name=IsActive]').checked=true;f.requestSubmit();})()");
  await waitPath('/Admin/Plans');
  activeBase=base;await navigate('/Wallet');
  await fill({Amount:'1000'});
  await evaluate("document.querySelector('form[action*=StartDemoPayment]').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Wallet/DemoPayment/') && document.readyState==='complete'");
  await evaluate("document.querySelector('button[name=outcome][value=Paid]').click()");
  await waitPath('/Wallet');
  await navigate('/Home/Membership');
  await evaluate("window.confirm=()=>true;document.querySelector('input[name=planId][value=monthly]').form.requestSubmit()");
  await waitFor("location.pathname.startsWith('/Receipts/Details/RC-MB-') && document.readyState==='complete'");
  const membershipReceiptPath=await evaluate('location.pathname');
  await screenshot('50-membership-receipt');
  if(!await evaluate("document.querySelector('.receipt-document').innerText.includes('100.00')")) throw new Error('Membership receipt is missing purchased price');
  await call('Emulation.setEmulatedMedia',{media:'print'});
  const membershipPdf=await call('Page.printToPDF',{printBackground:false,preferCSSPageSize:true});
  await writeFile(path.join(output,'membership-receipt.pdf'),Buffer.from(membershipPdf.data,'base64'));
  await call('Emulation.setEmulatedMedia',{media:'screen'});
  for(const width of [360,768]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<680});
    for(const route of ['/Receipts','/Receipts/Details/RC-BK-000001',membershipReceiptPath]) {
      await navigate(route);
      if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Receipt overflow at ${width}: ${route}`);
    }
  }
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await navigate('/Wallet');
  if(await evaluate("document.querySelector('form[action=\"/Wallet/Buy\"]') !== null || document.querySelector('main').innerText.includes('MEMBERSHIP PLANS')")) throw new Error('Membership offers remain on wallet page');
  await screenshot('51-wallet-without-plans');
  await navigate('/Receipts');
  const receiptRow=await evaluate("(() => {const r=document.querySelector('tbody tr').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
  await call('Input.dispatchMouseEvent',{type:'mouseMoved',x:receiptRow.x,y:receiptRow.y});
  if(!await evaluate("getComputedStyle(document.querySelector('tbody tr')).backgroundColor==='rgb(38, 56, 32)'")) throw new Error('Table hover does not use the dark green theme');
  await screenshot('52-receipt-row-hover');
  await evaluate("document.querySelector('tbody tr a').focus()");
  await call('Input.dispatchMouseEvent',{type:'mouseMoved',x:0,y:0});
  if(!await evaluate("getComputedStyle(document.querySelector('tbody tr')).backgroundColor==='rgb(38, 56, 32)'")) throw new Error('Keyboard focus row contrast is missing');
  activeBase=base;await navigate('/Accounting');
  await screenshot('53-customer-accounting');
  await fill({BookingId:'1',Reason:'Unable to attend; please refund booking'});
  await evaluate("document.querySelector('form[action*=RequestRefund]').requestSubmit()");
  await waitPath('/Accounting');
  activeBase=adminBase;await navigate('/Admin/Refunds');
  await screenshot('54-admin-refund-pending');
  await evaluate("document.querySelector('button[name=approve][value=true]').click()");
  await waitPath('/Admin/Refunds');
  if(!await evaluate("document.querySelector('main').innerText.includes('คืนเข้ากระเป๋าแล้ว')")) throw new Error('Refund approval did not complete');
  await navigate('/Admin/Expenses');
  await fill({Payee:'Court maintenance team',Amount:'150',Reference:'BROWSER-EXP-1',Description:'Replace damaged court net'});
  await evaluate("document.querySelector('form[method=post][action=\"/Admin/Expenses\"]').requestSubmit()");
  await waitPath('/Admin/Expenses');
  await screenshot('55-admin-expenses');
  await navigate('/Admin/Accounting');
  await screenshot('56-admin-accounting');
  for(const width of [360,768]) {
    await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<680});
    for(const route of ['/Admin/Accounting','/Admin/Expenses','/Admin/Refunds']) {
      await navigate(route);
      if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Accounting overflow at ${width}: ${route}`);
    }
    activeBase=base;await navigate('/Accounting');
    if(await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error(`Customer accounting overflow at ${width}`);
    if(await evaluate("getComputedStyle(document.querySelector('#refunds .empty')).backgroundColor==='rgb(255, 255, 255)'")) throw new Error('Accounting empty state lost dark theme');
    await screenshot(`57-customer-accounting-${width}`);
    activeBase=adminBase;
  }
  activeBase=adminBase;
  await call('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await navigate('/Admin/Plans');
  await evaluate("(() => {const f=document.querySelector('input[name=Id][value=monthly]').form;f.querySelector('[name=MonthlyHours]').value='2';const sports=f.querySelector('[name=QuotaSportTypeId]');sports.value=sports.options[1].value;f.requestSubmit();})()");
  await waitPath('/Admin/Plans');await screenshot('59-admin-quota-setup');
  activeBase=secondCustomerBase;await navigate('/Account/Register');
  await fill({Name:'สมาชิกโควตา',Email:'quota-ui@example.com',Password:password,ConfirmPassword:password});await submit('/Account/Login');
  await fill({Email:'quota-ui@example.com',Password:password});await submit('/Courts');
  await navigate('/Wallet');await fill({Amount:'1000'});
  await evaluate("document.querySelector('form[action*=StartDemoPayment]').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Wallet/DemoPayment/') && document.readyState==='complete'");
  await evaluate("document.querySelector('button[name=outcome][value=Paid]').click()");await waitPath('/Wallet');
  await navigate('/Home/Membership');await screenshot('60-quota-plan-offers');
  await evaluate("window.confirm=()=>true;document.querySelector('input[name=planId][value=monthly]').form.requestSubmit()");
  await waitFor("location.pathname.startsWith('/Receipts/Details/RC-MB-') && document.readyState==='complete'");
  await navigate('/Home/Membership');await screenshot('61-my-quota');
  if(!await evaluate("document.querySelector('main').innerText.includes('ชั่วโมงสนามของฉัน')"))throw new Error('Membership quota balance is missing');
  await navigate('/Bookings/Create?courtId=1');
  await evaluate("(() => {const f=document.getElementById('booking-form');const inputs=[...f.querySelectorAll('[name=Hours]')];const first=inputs.findIndex((i,n)=>!i.disabled && inputs[n+1] && !inputs[n+1].disabled && Number(inputs[n+1].value)===Number(i.value)+1);if(first<0)throw new Error('Test needs two consecutive available hours');inputs.forEach((i,n)=>i.checked=n===first||n===first+1);f.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await waitFor("document.querySelector('[name=ExpectedIncludedHours]').value==='2'");
  if(!await evaluate("document.getElementById('quote-message').innerText.includes('ใช้โควตา')"))throw new Error('Quote does not explain included hours');
  await screenshot('62-booking-with-quota');
  await evaluate("document.getElementById('booking-form').requestSubmit()");
  await waitFor("location.pathname.startsWith('/Bookings/Result/') && document.readyState==='complete'");
  if(!await evaluate("document.querySelector('main').innerText.includes('ใช้สิทธิ์แพ็กแล้ว') && !!document.querySelector('a[href*=RC-BK]')"))throw new Error('Quota booking lacks settlement evidence');
  for(const width of [360,768]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:950,deviceScaleFactor:1,mobile:width<680});
    await navigate('/Home/Membership');
    if(await evaluate('document.documentElement.scrollWidth>innerWidth+1'))throw new Error(`Quota membership overflow at ${width}`);
    await screenshot(`63-quota-mobile-${width}`);
  }
  if(clientErrors.length) throw new Error('Browser errors: '+clientErrors.join(', '));
  await writeFile(path.join(output,'verification.json'),JSON.stringify({pages:13,extraForms:3,mobileWidths:[390],bookingNetBaht:510,
    javascriptErrors:clientErrors,productionDatabaseChanged:false,source:'Real MVC views, isolated SQLite, Testing environment'},null,2));
  console.log('PASS: QR shows discounted amount, private transfer proof awaits admin review, manual payment confirmation, promotions, booking, cancellation, image uploads and responsive layouts; no JS errors.');
  await call('Browser.close').catch(()=>{});
} finally {
  ws?.close();
  if(server && server.exitCode===null) server.kill();
  if(browser && browser.exitCode===null) browser.kill();
  await pause(700);
  const resolved = path.resolve(temp);
  if(resolved.startsWith(path.resolve(tmpdir())+path.sep) && path.basename(resolved).startsWith('courtbook-browser-'))
    await rm(resolved,{recursive:true,force:true,maxRetries:5,retryDelay:300}).catch(()=>{});
}
