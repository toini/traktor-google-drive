import type { Page } from '@playwright/test';

export type CastLoad = {
  url: string;
  contentType: string;
  title: string;
  artist: string;
  currentTime: number;
  streamType: string;
};

export type CastTestState = {
  connected: boolean;
  deviceName: string | null;
  playerState: string | null;
  currentTime: number;
  duration: number;
  loads: CastLoad[];
  failNext: string | null;
  optionsSeen: { receiverApplicationId?: string; autoJoinPolicy?: string } | null;
};

declare global {
  interface Window {
    /** Test control surface installed alongside the fake SDK. */
    __castTest: {
      state: CastTestState;
      /** Move the playback position, as the device would while playing. */
      tick(seconds: number): void;
      /** Reject the next loadMedia with this error code. */
      failNextLoad(code: string): void;
    };
  }
}

export const CAST_DEVICE_NAME = 'Living Room TV';

/**
 * Stands in for the Cast Web Sender SDK. Real casting needs Chrome (not
 * Chromium) plus a device on the LAN, so the sender can only be tested against a
 * fake — cast.js finds `window.cast.framework` already present and skips
 * fetching the real one.
 *
 * Deliberately written without TypeScript-only syntax inside the init script:
 * Playwright serialises the function, so anything needing a downlevel transform
 * is a risk not worth taking.
 */
export async function mockCastSdk(page: Page, deviceName = CAST_DEVICE_NAME): Promise<void> {
  await page.addInitScript((name) => {
    const state = {
      connected: false,
      deviceName: null,
      playerState: null,
      currentTime: 0,
      duration: 0,
      loads: [],
      failNext: null,
      optionsSeen: null,
    };

    const listeners = [];
    const fire = () => listeners.forEach((l) => l());

    function MediaInfo(contentId, contentType) {
      this.contentId = contentId;
      this.contentType = contentType;
      this.streamType = '';
      this.metadata = null;
    }

    function MusicTrackMediaMetadata() {
      this.title = '';
      this.artist = '';
    }

    function LoadRequest(media) {
      this.media = media;
      this.autoplay = false;
      this.currentTime = 0;
    }

    const session = {
      getCastDevice: () => ({ friendlyName: state.deviceName }),
      loadMedia: (request) => {
        state.loads.push({
          url: request.media.contentId,
          contentType: request.media.contentType,
          title: request.media.metadata?.title ?? '',
          artist: request.media.metadata?.artist ?? '',
          currentTime: request.currentTime,
          streamType: request.media.streamType,
        });

        if (state.failNext) {
          const code = state.failNext;
          state.failNext = null;
          return Promise.reject(code);
        }

        state.playerState = 'PLAYING';
        state.currentTime = request.currentTime;
        // A recorded set is long; two hours keeps the seek bar realistic.
        state.duration = 7200;
        fire();
        return Promise.resolve();
      },
    };

    const context = {
      setOptions: (options) => { state.optionsSeen = options; },
      requestSession: () => {
        state.connected = true;
        state.deviceName = name;
        fire();
        return Promise.resolve();
      },
      getCurrentSession: () => (state.connected ? session : null),
      endCurrentSession: () => {
        state.connected = false;
        state.deviceName = null;
        state.playerState = null;
        state.currentTime = 0;
        state.duration = 0;
        fire();
      },
    };

    // cast.js reads these as live properties, so they must be getters over the
    // same state the controller mutates.
    function RemotePlayer() {}
    Object.defineProperties(RemotePlayer.prototype, {
      isConnected: { get: () => state.connected },
      playerState: { get: () => state.playerState },
      duration: { get: () => state.duration },
      currentTime: {
        get: () => state.currentTime,
        set: (value) => { state.currentTime = value; },
      },
    });

    function RemotePlayerController() {}
    RemotePlayerController.prototype.addEventListener = (_type, handler) => listeners.push(handler);
    RemotePlayerController.prototype.playOrPause = () => {
      state.playerState = state.playerState === 'PLAYING' ? 'PAUSED' : 'PLAYING';
      fire();
    };
    RemotePlayerController.prototype.stop = () => {
      state.playerState = 'IDLE';
      fire();
    };
    RemotePlayerController.prototype.seek = () => fire();

    window['chrome'] = {
      cast: {
        AutoJoinPolicy: { ORIGIN_SCOPED: 'origin_scoped' },
        media: {
          DEFAULT_MEDIA_RECEIVER_APP_ID: 'CC1AD845',
          StreamType: { BUFFERED: 'BUFFERED' },
          MediaInfo,
          MusicTrackMediaMetadata,
          LoadRequest,
        },
      },
    };

    window['cast'] = {
      framework: {
        CastContext: { getInstance: () => context },
        RemotePlayer,
        RemotePlayerController,
        RemotePlayerEventType: { ANY_CHANGE: 'anyChanged' },
      },
    };

    window['__castTest'] = {
      state,
      tick: (seconds) => { state.currentTime = seconds; fire(); },
      failNextLoad: (code) => { state.failNext = code; },
    };
  }, deviceName);
}

/** Reads the recorded loadMedia calls out of the page. */
export function castLoads(page: Page): Promise<CastLoad[]> {
  return page.evaluate(() => window.__castTest.state.loads);
}

/** Reads the live fake-device state out of the page. */
export function castState(page: Page): Promise<CastTestState> {
  return page.evaluate(() => window.__castTest.state);
}
