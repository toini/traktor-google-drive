// SoundCloud's official embed player, driven through its Widget API
// (https://developers.soundcloud.com/docs/api/html5-widget). Used by a crate's
// Listen tab for tracks that are only on SoundCloud (ADR 20261025-54, "Crates").
// The API script loads on the first SoundCloud play, never before.

let api = null;

function loadApi() {
  if (window.SC && window.SC.Widget) return Promise.resolve();
  if (!api) {
    api = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = 'https://w.soundcloud.com/player/api.js';
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => {
        api = null;
        reject(new Error('The SoundCloud player did not load'));
      };
      document.head.appendChild(script);
    });
  }
  return api;
}

const widgets = new WeakMap();

/** Binds the widget in an iframe and reports its events to .NET (OnWidget). */
export async function attach(iframe, dotnet) {
  await loadApi();
  const widget = window.SC.Widget(iframe);
  const events = window.SC.Widget.Events;
  const send = (name) => dotnet.invokeMethodAsync('OnWidget', name).catch(() => {});
  widget.bind(events.READY, () => {
    send('ready');
    widget.play();
  });
  widget.bind(events.PLAY, () => send('play'));
  widget.bind(events.PAUSE, () => send('pause'));
  widget.bind(events.FINISH, () => send('finish'));
  widget.bind(events.ERROR, () => send('error'));
  widgets.set(iframe, widget);
}

export function play(iframe) {
  widgets.get(iframe)?.play();
}

export function pause(iframe) {
  widgets.get(iframe)?.pause();
}
