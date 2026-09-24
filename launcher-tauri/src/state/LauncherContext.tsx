import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { invoke } from "@tauri-apps/api/core";

/** Shape of the backend `LauncherConfig` as returned by `read_config`. */
export interface LauncherConfig {
  storefront?: string | null;
  modded_install_path: string;
  debug_mode: boolean;
  auto_post_lobby: boolean;
  discord_access_token: string;
  username: string;
  avatar_url: string;
  last_seen_version: string;
  profiles: Array<{
    name: string;
    mods: Array<{
      name: string;
      version?: string | null;
      file_hash?: string | null;
      download_url?: string | null;
    }>;
  }>;
  library: Array<{ path: string; storefront?: string | null }>;
}

export interface UserInfo {
  id: string;
  username: string;
  global_name: string | null;
  avatar: string | null;
}

interface LauncherContextValue {
  /** Latest config, or `null` until the initial read resolves. */
  config: LauncherConfig | null;
  /**
   * Merge a partial update into the current config and persist it via
   * `write_config`. Optimistically applied, rolled back on failure, and
   * serialized so concurrent updates can't clobber each other.
   */
  updateConfig: (partial: Partial<LauncherConfig>) => Promise<void>;
  /** Re-read the backend config (e.g. after backend-side mutations). */
  refreshConfig: () => Promise<LauncherConfig | null>;
  /**
   * Monotonic counter bumped whenever backend state changed and mounted pages
   * should re-fetch. Key effects on this (not on `modStatus`) to avoid fetch
   * loops from unrelated re-renders.
   */
  refreshNonce: number;
  /** Ask every mounted page to re-fetch its backend-derived data. */
  bumpRefresh: () => void;
  /** `null` while the initial auth check is still in flight. */
  loggedIn: boolean | null;
  username: string;
  avatarUrl: string;
  /** Record a successful Discord login and refresh persisted credentials. */
  login: (user: UserInfo) => Promise<void>;
  /** Clear credentials (backend + local) and switch the app to logged-out. */
  logout: () => Promise<void>;
}

const LauncherContext = createContext<LauncherContextValue | null>(null);

export function useLauncher(): LauncherContextValue {
  const value = useContext(LauncherContext);
  if (!value) {
    throw new Error("useLauncher must be used within a LauncherProvider");
  }
  return value;
}

function readConfigWithTimeout(): Promise<LauncherConfig> {
  // Don't let a slow config read hang first paint.
  return Promise.race([
    invoke<LauncherConfig>("read_config"),
    new Promise<never>((_, reject) =>
      setTimeout(() => reject(new Error("config timeout")), 3000)
    ),
  ]);
}

export function LauncherProvider({ children }: { children: ReactNode }) {
  const [config, setConfig] = useState<LauncherConfig | null>(null);
  const [loggedIn, setLoggedIn] = useState<boolean | null>(null);
  const [username, setUsername] = useState("");
  const [avatarUrl, setAvatarUrl] = useState("");
  const [refreshNonce, setRefreshNonce] = useState(0);
  const configRef = useRef<LauncherConfig | null>(null);
  const writeChain = useRef<Promise<void>>(Promise.resolve());

  const applyConfig = useCallback((next: LauncherConfig) => {
    configRef.current = next;
    setConfig(next);
  }, []);

  // Initial load: config is the source of truth for auth state.
  useEffect(() => {
    let cancelled = false;
    readConfigWithTimeout()
      .then((next) => {
        if (cancelled) return;
        applyConfig(next);
        const hasToken = next.discord_access_token.length > 0;
        setLoggedIn(hasToken);
        if (hasToken) {
          setUsername(next.username);
          setAvatarUrl(next.avatar_url);
        }
      })
      .catch(() => {
        if (!cancelled) setLoggedIn(false);
      });
    return () => {
      cancelled = true;
    };
  }, [applyConfig]);

  const refreshConfig = useCallback(async (): Promise<LauncherConfig | null> => {
    try {
      const next = await readConfigWithTimeout();
      applyConfig(next);
      return next;
    } catch {
      return configRef.current;
    }
  }, [applyConfig]);

  // Shared refresh signal. Consumers key effects on the nonce; bumping it is
  // cheap and side-effect-free beyond re-running those effects.
  const bumpRefresh = useCallback(() => {
    setRefreshNonce((n) => n + 1);
  }, []);

  const updateConfig = useCallback(
    (partial: Partial<LauncherConfig>): Promise<void> => {
      const prev = configRef.current;
      if (!prev) {
        return Promise.reject(new Error("Settings are not loaded yet"));
      }
      const merged: LauncherConfig = { ...prev, ...partial };
      configRef.current = merged;
      setConfig(merged);

      // `write_config` replaces the whole object, so serialize writes: two
      // concurrent partial updates must not land out of order (lost update).
      const run = writeChain.current.then(() =>
        invoke("write_config", { newConfig: merged })
      );
      writeChain.current = run.then(
        () => undefined,
        () => undefined
      );
      return run
        .then(() => undefined)
        .catch((e: unknown) => {
          // Roll back only if no newer update superseded this one.
          if (configRef.current === merged) {
            configRef.current = prev;
            setConfig(prev);
          }
          throw e;
        });
    },
    []
  );

  const login = useCallback(
    async (user: UserInfo) => {
      setUsername(user.username);
      setAvatarUrl(
        user.avatar
          ? `https://cdn.discordapp.com/avatars/${user.id}/${user.avatar}.png`
          : ""
      );
      setLoggedIn(true);
      // `login_discord` persisted fresh credentials in the backend config;
      // re-read so later whole-object writes can't clobber the new token.
      await refreshConfig();
    },
    [refreshConfig]
  );

  const logout = useCallback(async () => {
    // Only commit the auth-state flip once `write_config` SUCCEEDED (the same
    // rollback pattern `updateConfig` uses). Flipping in `finally` used to log
    // the UI out even when persistence failed — WelcomeView, but a restart
    // restored the old token. On failure the rejection propagates so the
    // caller's existing failure toast fires and the user stays logged in
    // (config was rolled back by `updateConfig`, so UI and disk agree again).
    await updateConfig({
      discord_access_token: "",
      username: "",
      avatar_url: "",
    });
    setUsername("");
    setAvatarUrl("");
    setLoggedIn(false);
  }, [updateConfig]);

  const value: LauncherContextValue = {
    config,
    updateConfig,
    refreshConfig,
    refreshNonce,
    bumpRefresh,
    loggedIn,
    username,
    avatarUrl,
    login,
    logout,
  };

  return (
    <LauncherContext.Provider value={value}>
      {children}
    </LauncherContext.Provider>
  );
}
