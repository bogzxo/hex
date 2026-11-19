// The request dialog opens and closes from its buttons' own command and commandfor, with nothing here. This is only for
// a browser older than those (before Safari 26.2 or Firefox 144), and for one without closedby (Safari), where a press
// outside the dialog wouldn't close it. Nothing is loaded from anywhere else.

if (!('commandForElement' in HTMLButtonElement.prototype)) {
  for (const button of document.querySelectorAll('button[commandfor][command]')) {
    const dialog = document.getElementById(button.getAttribute('commandfor'));
    if (!(dialog instanceof HTMLDialogElement)) continue;
    button.addEventListener('click', () => {
      const command = button.getAttribute('command');
      if (command === 'show-modal' && !dialog.open) dialog.showModal();
      else if (command === 'close') dialog.close();
    });
  }
}

if (!('closedBy' in HTMLDialogElement.prototype)) {
  // The pointer's place, not the event's target: the dialog is the target of its own padding as well as the backdrop.
  // Pressed and let go outside, so a drag that ends on the backdrop doesn't close it
  const outside = (event, dialog) => {
    const box = dialog.getBoundingClientRect();
    return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom;
  };
  for (const dialog of document.querySelectorAll('dialog[closedby="any"]')) {
    let pressedOutside = false;
    dialog.addEventListener('pointerdown', (event) => { pressedOutside = outside(event, dialog); });
    dialog.addEventListener('pointerup', (event) => { if (pressedOutside && outside(event, dialog)) dialog.close(); });
  }
}
