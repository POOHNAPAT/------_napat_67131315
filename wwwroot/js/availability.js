(() => {
  const clock = document.querySelector('[data-server-now]');
  if (!clock) return;
  // Advance the server's timestamp rather than relying on the phone's date or time zone.
  const serverNow = Number(clock.dataset.serverNow);
  const observedAt = performance.now();
  function expireSlots() {
    const now = serverNow + performance.now() - observedAt;
    let changed = false;
    for (const element of clock.querySelectorAll('[data-slot-start]')) {
      if (Date.parse(element.dataset.slotStart) > now) continue;
      if (element.matches('input:not(:disabled)')) {
        element.disabled = true;
        element.checked = false;
        const label = element.closest('.slot-choice');
        label.classList.add('disabled');
        label.querySelector('small').textContent = 'ผ่านไปแล้ว';
        changed = true;
      } else if (element.matches('a.available')) {
        element.removeAttribute('href');
        element.setAttribute('aria-disabled', 'true');
        element.tabIndex = -1;
        element.classList.replace('available', 'unavailable');
        element.querySelector('span').textContent = 'ผ่านไปแล้ว';
        changed = true;
      }
    }
    if (changed) clock.dispatchEvent(new Event('slots-expired', { bubbles: true }));
    return changed;
  }
  clock.addEventListener('submit', event => { if (expireSlots()) event.preventDefault(); });
  window.addEventListener('focus', expireSlots);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) expireSlots(); });
  setInterval(expireSlots, 15000);
  expireSlots();
})();
