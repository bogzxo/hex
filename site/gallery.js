// The gallery's pictures open in a dialog instead of going off to the file. Without this they stay plain links.
const dialog = document.querySelector('dialog.lightbox');
const links = [...document.querySelectorAll('.gallery a')];

if (dialog && typeof dialog.showModal === 'function' && links.length > 0) {
  const picture = dialog.querySelector('img');
  const caption = dialog.querySelector('p');
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  let current = -1;

  const fill = (index) => {
    current = (index + links.length) % links.length;
    const link = links[current];
    const small = link.querySelector('img');
    picture.src = link.href;
    picture.alt = small.alt;
    caption.textContent = link.closest('figure').querySelector('figcaption').textContent.trim();
  };

  // The picture is the same thing in the gallery and in the dialog, so it's the one name that moves between them
  const morph = (small, change) => {
    if (!document.startViewTransition || reduced.matches) {
      change();
      return;
    }
    small.style.viewTransitionName = 'shot';
    // The big picture is waited for, so what grows out of the small one is the picture and not an empty box
    const transition = document.startViewTransition(async () => {
      small.style.viewTransitionName = '';
      change();
      try { await picture.decode(); } catch { /* it shows when it comes */ }
    });
    transition.finished.finally(() => {
      small.style.viewTransitionName = '';
      picture.style.viewTransitionName = '';
    });
  };

  links.forEach((link, index) => {
    link.addEventListener('click', (event) => {
      // A click that asks for a new tab or window gets one
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      fill(index);
      morph(link.querySelector('img'), () => {
        picture.style.viewTransitionName = 'shot';
        dialog.showModal();
      });
    });
  });

  dialog.querySelector('[data-step="-1"]').addEventListener('click', () => fill(current - 1));
  dialog.querySelector('[data-step="1"]').addEventListener('click', () => fill(current + 1));
  dialog.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowLeft') fill(current - 1);
    if (event.key === 'ArrowRight') fill(current + 1);
  });

  // Closing puts it back where it came from. The browser hands the focus back to the link by itself
  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    shut();
  });
  dialog.querySelector('form').addEventListener('submit', (event) => {
    event.preventDefault();
    shut();
  });

  function shut() {
    if (!document.startViewTransition || reduced.matches || current < 0) {
      dialog.close();
      return;
    }
    const small = links[current].querySelector('img');
    picture.style.viewTransitionName = 'shot';
    const transition = document.startViewTransition(() => {
      picture.style.viewTransitionName = '';
      small.style.viewTransitionName = 'shot';
      dialog.close();
    });
    transition.finished.finally(() => {
      small.style.viewTransitionName = '';
    });
  }

  // closedby="any" closes on a click outside where the browser knows it. Where it doesn't, the same by hand:
  // a press and a release both outside the dialog's box, so dragging a selection out of it closes nothing
  if (!('closedBy' in HTMLDialogElement.prototype)) {
    const outside = (event) => {
      const box = dialog.getBoundingClientRect();
      return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom;
    };
    let pressedOutside = false;
    dialog.addEventListener('pointerdown', (event) => { pressedOutside = outside(event); });
    dialog.addEventListener('pointerup', (event) => { if (pressedOutside && outside(event)) shut(); });
  }
}
