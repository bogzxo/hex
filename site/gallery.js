// The image links still work on their own if the browser cannot open a modal.
(() => {
  const dialog = document.querySelector('dialog.lightbox');
  const links = [...document.querySelectorAll('.gallery a')];
  if (!dialog || typeof dialog.showModal !== 'function' || !links.length) return;

  const picture = dialog.querySelector('img');
  const error = dialog.querySelector('.lightbox-error');
  const caption = dialog.querySelector('.lightbox-caption');
  const position = dialog.querySelector('.lightbox-position');
  const original = dialog.querySelector('.lightbox-original');
  let current = 0;

  function showPicture(index) {
    current = (index + links.length) % links.length;
    const link = links[current];
    const thumbnail = link.querySelector('img');
    position.textContent = `Screenshot ${current + 1} of ${links.length}`;
    caption.replaceChildren(...Array.from(
      link.closest('figure').querySelector('figcaption').childNodes,
      node => node.cloneNode(true)
    ));
    original.href = link.href;
    picture.alt = thumbnail.alt;
    error.textContent = '';
    picture.src = link.href;
  }

  picture.addEventListener('error', () => {
    error.textContent = 'Could not load this screenshot.';
  });

  links.forEach((link, index) => {
    link.setAttribute('aria-haspopup', 'dialog');
    link.addEventListener('click', event => {
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      showPicture(index);
      link.focus({ preventScroll: true });
      dialog.showModal();
    });
  });

  const previous = dialog.querySelector('[data-step="-1"]');
  const next = dialog.querySelector('[data-step="1"]');
  previous.disabled = next.disabled = links.length === 1;
  previous.addEventListener('click', () => showPicture(current - 1));
  next.addEventListener('click', () => showPicture(current + 1));
  dialog.addEventListener('keydown', event => {
    if (links.length === 1 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    showPicture(current + (event.key === 'ArrowLeft' ? -1 : 1));
  });

  // Safari versions without closedby still get backdrop dismissal. A drag
  // from inside the dialog to the backdrop should keep the dialog open.
  if (!('closedBy' in HTMLDialogElement.prototype)) {
    const outside = event => {
      const box = dialog.getBoundingClientRect();
      return event.clientX < box.left || event.clientX > box.right ||
        event.clientY < box.top || event.clientY > box.bottom;
    };
    let pressedOutside = false;
    dialog.addEventListener('pointerdown', event => { pressedOutside = outside(event); });
    dialog.addEventListener('pointerup', event => {
      if (pressedOutside && outside(event)) dialog.close();
      pressedOutside = false;
    });
  }
})();
