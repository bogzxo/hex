// The Day/Night switch in the bar. Two states: the system's look (nothing kept), and the other one pinned (kept as
// "light" or "dark"). Pressed, it goes to the opposite of what's on screen; that being the system's own look, the pin
// is let go of rather than kept, so the page follows the system again. A pin isn't let go of because the system later
// came round to it, a system that goes dark at night would have made pinning impossible. The <meta> is what the
// stylesheet follows, set before anything's drawn by the line in the page's head. A plain script, so it's in a function
// of its own to keep its names off the page's.

(() => {
const KEY = 'color-scheme';
const meta = document.querySelector('meta[name="color-scheme"]');
const button = document.getElementById('scheme');
const systemDark = matchMedia('(prefers-color-scheme: dark)');

// The browser's bar in the page's colour, the system's by its media queries unless a look is pinned
const bars = [...document.querySelectorAll('meta[name="theme-color"]')].map(bar => ({ bar, media: bar.getAttribute('media') }));
const BAR = { light: '#ffffff', dark: '#18171f' };

const system = () => (systemDark.matches ? 'dark' : 'light');
const shown = () => (meta.content === 'light' || meta.content === 'dark' ? meta.content : system());

function updateButton() {
  const dark = shown() === 'dark';
  const label = dark ? 'Switch to Solstice Day' : 'Switch to Solstice Night';
  button.setAttribute('aria-label', label);
  button.title = label;
  button.querySelector('.to-day').toggleAttribute('hidden', !dark);
  button.querySelector('.to-night').toggleAttribute('hidden', dark);
}

function apply(pinned) {
  meta.content = pinned ?? 'light dark';
  updateButton();
  for (const { bar, media } of bars) {
    if (pinned) {
      bar.removeAttribute('media');
      bar.content = BAR[pinned];
    } else {
      if (media) bar.setAttribute('media', media);
      bar.content = media?.includes('dark') ? BAR.dark : BAR.light;
    }
  }
}

function keep(pinned) {
  try {
    if (pinned) localStorage.setItem(KEY, pinned);
    else localStorage.removeItem(KEY);
  } catch {
    // Kept for this visit only where the site's storage is blocked, the look still changes
  }
}

if (meta && button) {
  apply(meta.content === 'light' || meta.content === 'dark' ? meta.content : null);
  button.hidden = false;
  // The whole page fades from one look to the other, the old one a picture over the new one going out (a view
  // transition). Where there's none, or less motion was asked for, it changes at once
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  button.addEventListener('click', () => {
    const target = shown() === 'dark' ? 'light' : 'dark';
    const pinned = target === system() ? null : target;
    keep(pinned);
    if (document.startViewTransition && !reduced.matches) document.startViewTransition(() => apply(pinned));
    else apply(pinned);
  });

  systemDark.addEventListener('change', updateButton);

  // Switched in another tab of the page, this one follows
  addEventListener('storage', event => {
    if (event.key === KEY || event.key === null) apply(event.newValue === 'light' || event.newValue === 'dark' ? event.newValue : null);
  });
}
})();
