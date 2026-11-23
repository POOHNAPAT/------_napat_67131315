(() => {
  const input = document.getElementById('Image');
  if (!input) return;
  const preview = document.getElementById('court-image-preview');
  const empty = document.getElementById('court-image-empty');
  const feedback = document.getElementById('image-feedback');
  const remove = document.getElementById('RemoveImage');
  const original = preview.getAttribute('src');
  let objectUrl;
  function update() {
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    objectUrl = null;
    const file = input.files[0];
    const valid = !file || (['image/jpeg', 'image/png', 'image/webp'].includes(file.type) && file.size > 0 && file.size <= 5 * 1024 * 1024);
    input.setCustomValidity(valid ? '' : 'กรุณาเลือก JPG, PNG หรือ WebP ขนาดไม่เกิน 5 MB');
    feedback.textContent = valid ? (file ? file.name + ' · พร้อมบันทึก' : '') : input.validationMessage;
    feedback.classList.toggle('error', !valid);
    const src = valid && file ? (objectUrl = URL.createObjectURL(file)) : remove?.checked ? null : original;
    if (src) preview.src = src;
    else preview.removeAttribute('src');
    preview.hidden = !src;
    empty.hidden = !!src;
  }
  input.addEventListener('change', update);
  remove?.addEventListener('change', update);
  update();
})();
