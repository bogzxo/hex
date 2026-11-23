// The page's own behaviour, all of it extra: without it the menu still opens (popovertarget), the rails still scroll and
// every link still goes where it says. A plain script, in a function of its own to keep its names off the page's.

(() => {
const reduced = matchMedia('(prefers-reduced-motion: reduce)');

/* The menu. A link pressed in it closes it, and it's closed when the bar has room for the links again */

const menu = document.getElementById('site-menu');
const wide = matchMedia('(width >= 62rem)');
if (menu && typeof menu.hidePopover === 'function') {
  const close = () => { if (menu.matches(':popover-open')) menu.hidePopover(); };
  menu.addEventListener('click', event => { if (event.target.closest('a')) close(); });
  wide.addEventListener('change', close);
  // Open or closed, said on the button as well as by the popover's own wiring
  const opener = document.querySelector('[popovertarget="site-menu"]');
  opener?.setAttribute('aria-expanded', 'false');
  menu.addEventListener('toggle', event => opener?.setAttribute('aria-expanded', String(event.newState === 'open')));
}

/* Where you are. The part of the page on screen is marked in the bar's list, by the line at a third of the way down */

const links = new Map([...document.querySelectorAll('.site-menu a[href^="#"]')].map(link => [link.hash.slice(1), link]));
const parts = [...links.keys()].map(id => document.getElementById(id)).filter(Boolean);
if ('IntersectionObserver' in window && parts.length) {
  let current = null;
  const mark = id => {
    if (id === current) return;
    current = id;
    for (const [key, link] of links) {
      if (key === id) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    }
  };
  const seen = new Set();
  const watch = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) seen.add(entry.target);
      else seen.delete(entry.target);
    }
    // The one furthest down of those crossing the line, so a short part after a long one gets its turn
    const on = parts.filter(part => seen.has(part)).at(-1);
    mark(on ? on.id : null);
  }, { rootMargin: '-33% 0px -66% 0px' });
  for (const part of parts) watch.observe(part);
}

/* The download for the system the page is opened on, a big button over the cards, and its card marked. The system
   from the browser's client hints where it has them (and the processor with them, which only Chromium says), its user
   agent where not. A Mac's chip is told by Chromium's client hints or by the graphics card the browser names (Apple M1
   against Intel, AMD); Safari only says Apple GPU, so a Mac that can't be told gets a button for each chip. Anything
   else is x64 unless it says ARM. Nothing guessed means no button, the cards say it all */

const yours = document.querySelector('.get-yours');
if (yours) {
  const NAMES = { windows: 'Windows', mac: 'macOS', linux: 'Linux' };
  const ua = navigator.userAgent;
  const hinted = navigator.userAgentData?.platform ?? '';
  const os = /Windows/i.test(hinted || ua) ? 'windows'
    : /mac/i.test(hinted || ua) && !/iPhone|iPad/i.test(ua) ? 'mac'
    : /Linux/i.test(hinted || ua) && !/Android/i.test(ua) ? 'linux' : null;

  // The chip of a Mac from the graphics card's name, null when the browser says no more than Apple GPU
  const macChip = () => {
    try {
      const gl = document.createElement('canvas').getContext('webgl');
      const info = gl?.getExtension('WEBGL_debug_renderer_info');
      const name = info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : '';
      if (/Apple M\d/i.test(name)) return 'arm64';
      if (/Intel|AMD|Radeon|NVIDIA|GeForce/i.test(name)) return 'x64';
    } catch {
      // No WebGL, no guess
    }
    return null;
  };

  const show = arch => {
    const card = document.querySelector(`.platform[data-os="${os}"]`);
    if (!card) return;
    const [setup, portable] = card.querySelectorAll('.pick');
    const link = pick => pick.querySelector(`a[data-arch="${arch}"]`) ?? pick.querySelector('a');
    // A Mac that couldn't be told: a button for each chip, for the disk images
    if (os === 'mac' && !arch) {
      card.classList.add('yours');
      const button = yours.querySelector('.button');
      const [apple, intel] = setup.querySelectorAll('a');
      button.href = apple.href;
      button.textContent = 'Download for Apple silicon';
      button.after(Object.assign(document.createElement('a'), { className: button.className, href: intel.href, textContent: 'Download for Intel' }));
      yours.querySelector('.note').replaceChildren('A Mac with an M1, M2 or newer is Apple silicon, older ones are Intel (Apple menu, About This Mac). Or the ',
        Object.assign(document.createElement('a'), { href: portable.querySelector('a').href, textContent: 'ZIP' }), ', or another system below.');
      yours.hidden = false;
      return;
    }
    const main = link(setup), zip = link(portable);
    card.classList.add('yours');
    const button = yours.querySelector('.button');
    button.href = main.href;
    button.textContent = `Download for ${NAMES[os]}`;
    const note = yours.querySelector('.note');
    note.replaceChildren(os === 'mac' ? `Disk image, ${main.textContent}. Or the ` : `Setup, ${main.textContent}. Or the `,
      Object.assign(document.createElement('a'), { href: zip.href, textContent: os === 'mac' ? 'ZIP' : 'portable ZIP' }), ', or another system below.');
    yours.hidden = false;
  };

  // A phone or a tablet can't run Hex. It gets a way to send the page to a computer instead: the share sheet where
  // there is one, the link copied where there isn't
  const handheld = /Android|iPhone|iPad|Mobile/i.test(ua) || navigator.userAgentData?.mobile;
  if (!os && handheld && (navigator.share || navigator.clipboard)) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'button big';
    button.textContent = navigator.share ? 'Send the link to your computer' : 'Copy the download link';
    const note = yours.querySelector('.note');
    note.textContent = 'Hex runs on Windows, macOS and Linux.';
    note.setAttribute('role', 'status');
    const url = new URL('#get-it', location.href).href;
    button.addEventListener('click', async () => {
      try {
        if (navigator.share) await navigator.share({ title: 'Hex', url });
        else { await navigator.clipboard.writeText(url); note.textContent = 'Copied. Open it on your computer.'; }
      } catch {
        // Dismissed, nothing to say
      }
    });
    yours.querySelector('a.button').replaceWith(button);
    yours.hidden = false;
  }

  if (os) {
    const guess = os === 'mac' ? macChip() : /arm|aarch64/i.test(ua) ? 'arm64' : 'x64';
    if (navigator.userAgentData?.getHighEntropyValues) {
      navigator.userAgentData.getHighEntropyValues(['architecture'])
        .then(values => show(values.architecture === 'arm' ? 'arm64' : os === 'mac' ? (values.architecture === 'x86' ? 'x64' : guess) : 'x64'), () => show(guess));
    } else {
      show(guess);
    }
  }
}

/* The cards that turn. More shows the rest of a card in place of its sentence and Back turns it back, the card the
   same size either way (the stylesheet stacks both faces in one cell). The face out of sight is inert, and the
   keyboard goes to the button on the face that came round */

const flips = document.querySelectorAll('.flip');
if (flips.length) {
  document.documentElement.classList.add('flips');
  for (const card of flips) {
    const front = card.querySelector('.front');
    const back = card.querySelector('.back');
    const more = front.querySelector('.face-button');
    const ret = back.querySelector('.face-button');
    const turn = turned => {
      card.classList.toggle('turned', turned);
      more.setAttribute('aria-expanded', String(turned));
      front.inert = turned;
      back.inert = !turned;
      (turned ? ret : more).focus({ preventScroll: true });
    };
    back.inert = true;
    more.addEventListener('click', () => turn(true));
    ret.addEventListener('click', () => turn(false));
  }
}

/* The rails. A list that's a row you swipe on a phone (the stylesheet's .swipe) gets a slide's place said for a screen
   reader, Previous and Next, and dots or a count, all hidden while the list fits without scrolling (a wide screen,
   where it's a grid). Finite: the buttons stop at the ends rather than going round */

for (const track of document.querySelectorAll('.swipe')) {
  const slides = [...track.children];
  if (slides.length < 2) continue;

  const label = track.dataset.label || 'Slides';
  track.setAttribute('role', 'region');
  track.setAttribute('aria-roledescription', 'carousel');
  track.setAttribute('aria-label', label);
  slides.forEach((slide, index) => {
    slide.setAttribute('role', 'group');
    slide.setAttribute('aria-roledescription', 'slide');
    slide.setAttribute('aria-label', `${index + 1} of ${slides.length}`);
  });

  const controls = document.createElement('div');
  controls.className = 'swipe-controls';
  controls.hidden = true;
  const button = (step, name, path) => {
    const control = document.createElement('button');
    control.type = 'button';
    control.className = 'button small quiet icon-button';
    control.setAttribute('aria-label', name);
    control.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${path}"/></svg>`;
    control.addEventListener('click', () => go(at() + step));
    return control;
  };
  const previous = button(-1, `Previous, ${label}`, 'm15 18-6-6 6-6');
  const next = button(1, `Next, ${label}`, 'm9 18 6-6-6-6');

  // Dots for a few, a count for many, which wouldn't fit a phone as dots a finger can hit
  const dots = slides.length <= 5 ? slides.map((_, index) => {
    const dot = document.createElement('button');
    dot.type = 'button';
    dot.className = 'dot';
    dot.setAttribute('aria-label', `${index + 1} of ${slides.length}, ${label}`);
    dot.addEventListener('click', () => go(index));
    return dot;
  }) : null;
  const count = document.createElement('span');
  count.className = 'count';
  count.setAttribute('aria-hidden', 'true');
  const markers = document.createElement('div');
  markers.className = 'markers';
  markers.append(...(dots ?? [count]));

  const status = document.createElement('p');
  status.className = 'visually-hidden';
  status.setAttribute('role', 'status');
  controls.append(previous, markers, next);
  track.after(controls, status);

  // Where a slide starts in the track's own scroll, the snap lands it there
  const start = slide => slide.getBoundingClientRect().left - track.getBoundingClientRect().left + track.scrollLeft
    - parseFloat(getComputedStyle(track).scrollPaddingInlineStart || 0);
  const at = () => {
    if (pending !== null) return pending;
    let best = 0;
    slides.forEach((slide, index) => { if (Math.abs(start(slide) - track.scrollLeft) < Math.abs(start(slides[best]) - track.scrollLeft)) best = index; });
    // The end of the track can't scroll a last short slide to the start, it's the one on screen then
    return track.scrollLeft + track.clientWidth >= track.scrollWidth - 2 ? slides.length - 1 : best;
  };

  let pending = null, shown = -1, said = -1;
  function go(index) {
    index = Math.max(0, Math.min(slides.length - 1, index));
    pending = index;
    show(index);
    track.scrollTo({ left: start(slides[index]), behavior: reduced.matches ? 'instant' : 'smooth' });
  }

  function show(index) {
    if (index === shown) return;
    shown = index;
    dots?.forEach((dot, i) => dot.setAttribute('aria-current', i === index ? 'true' : 'false'));
    count.textContent = `${index + 1} / ${slides.length}`;
  }

  function settle() {
    pending = null;
    const index = at();
    show(index);
    for (const [control, off] of [[previous, index === 0], [next, index === slides.length - 1]]) {
      // A button that goes off with the keyboard on it hands it to the other one
      if (off && document.activeElement === control) (control === previous ? next : previous).focus();
      control.disabled = off;
    }
    if (index !== said) {
      if (said !== -1) status.textContent = `${index + 1} of ${slides.length}`;
      said = index;
    }
  }

  let frame = 0, debounce = 0;
  track.addEventListener('scroll', () => {
    if (pending === null && !frame) frame = requestAnimationFrame(() => { frame = 0; show(at()); });
    if (!('onscrollend' in window)) { clearTimeout(debounce); debounce = setTimeout(settle, 120); }
  }, { passive: true });
  if ('onscrollend' in window) track.addEventListener('scrollend', settle);

  // Only a rail while it scrolls: a grid on a wide screen has nothing to swipe and no controls
  const fit = () => {
    const scrolls = track.scrollWidth > track.clientWidth + 2;
    controls.hidden = !scrolls;
    if (scrolls) track.setAttribute('tabindex', '0');
    else track.removeAttribute('tabindex');
    settle();
  };
  new ResizeObserver(fit).observe(track);
}
// The demos drawn in the page stand still until somebody presses Play, then run once. The button is only there for
// somebody who hasn't asked for less motion: without it the demo is the picture it already was
const motion = matchMedia('(prefers-reduced-motion: no-preference)');
for (const play of document.querySelectorAll('.demo-play')) {
  const demo = document.getElementById(play.dataset.demo);
  if (!demo) continue;
  const offer = () => { play.hidden = !motion.matches; };
  offer();
  motion.addEventListener('change', offer);
  play.addEventListener('click', () => {
    // Taken off and put back so a second press starts it from the top
    demo.classList.remove('playing');
    void demo.offsetWidth;
    demo.classList.add('playing');
    play.textContent = 'Play it again';
  });
}
})();
