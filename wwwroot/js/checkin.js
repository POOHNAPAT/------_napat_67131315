(() => {
  const form=document.getElementById('checkin-lookup-form');
  if(!form)return;
  const message=document.getElementById('checkin-scan-status');
  const start=document.getElementById('start-checkin-camera');
  const stop=document.getElementById('stop-checkin-camera');
  const file=document.getElementById('checkin-qr-file');
  let resolved=false;
  if(typeof Html5Qrcode==='undefined'){
    message.textContent='โหลดตัวอ่าน QR ไม่สำเร็จ กรุณากรอกรหัสจองด้านล่าง';start.disabled=true;file.disabled=true;return;
  }
  const scanner=new Html5Qrcode('checkin-reader',{formatsToSupport:[Html5QrcodeSupportedFormats.QR_CODE],verbose:false});
  async function stopCamera(){
    if(scanner.isScanning)await scanner.stop();
    start.disabled=false;stop.disabled=true;
  }
  async function accept(value){
    if(resolved)return;
    resolved=true;
    try{await stopCamera();}catch{}
    document.getElementById('checkin-code').value=value;
    message.textContent='อ่าน QR แล้ว กำลังตรวจสอบรายการ ยังไม่บันทึกเช็กอิน';
    form.requestSubmit();
  }
  start.addEventListener('click',async()=>{
    start.disabled=true;file.disabled=true;
    message.textContent='กำลังเปิดกล้อง กรุณาอนุญาตการใช้งานกล้อง';
    try{
      scanner.clear();
      await scanner.start({facingMode:'environment'},{fps:8,qrbox:220},accept,()=>{});
      stop.disabled=false;message.textContent='วาง QR ของลูกค้าให้อยู่ในกรอบ';
    }catch{
      start.disabled=false;message.textContent='เปิดกล้องไม่ได้ กรุณาตรวจสิทธิ์กล้อง หรือเลือกรูป QR / กรอกรหัสจองแทน';
    }finally{file.disabled=false;}
  });
  stop.addEventListener('click',async()=>{
    try{await stopCamera();message.textContent='ปิดกล้องแล้ว';}catch{message.textContent='ปิดกล้องไม่สำเร็จ กรุณาโหลดหน้าใหม่';}
  });
  file.addEventListener('change',async()=>{
    const selected=file.files?.[0];if(!selected||resolved)return;
    if(selected.size>10*1024*1024){message.textContent='เลือกรูปขนาดไม่เกิน 10 MB';file.value='';return;}
    start.disabled=true;file.disabled=true;message.textContent='กำลังอ่าน QR จากรูป';
    try{
      await stopCamera();
      start.disabled=true;
      const text=await scanner.scanFile(selected,true);
      await accept(text);
    }catch{message.textContent='อ่าน QR ไม่ได้ กรุณาเลือกรูปที่ชัดเจน หรือกรอกรหัสจองแทน';}
    finally{start.disabled=false;file.disabled=false;file.value='';}
  });
  window.addEventListener('pagehide',()=>{if(scanner.isScanning)scanner.stop().catch(()=>{});});
})();
