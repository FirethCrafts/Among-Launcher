import { useNavigate, useLocation } from 'react-router-dom';
import { Home, Archive, Settings, Gamepad2, Users } from 'lucide-react';

const navItems = [
  { path: '/', label: 'Home', icon: Home },
  { path: '/library', label: 'Library', icon: Archive },
  { path: '/settings', label: 'Settings', icon: Settings },
];

interface SidebarProps {
  gameConnected: boolean;
  username: string;
  avatarUrl: string;
  /** App-level lobby membership: `true` = this machine hosts the lobby. */
  lobbyIsHost: boolean | null;
}

export function Sidebar({ gameConnected, username, avatarUrl, lobbyIsHost }: SidebarProps) {
  const navigate = useNavigate();
  const location = useLocation();

  // "In Game" is available to anyone connected. "Host Panel" only makes sense
  // while THIS machine actually hosts a lobby — a guest's panel is read-only,
  // so hide the item rather than tease it.
  const gameItems = [
    { path: '/ingame', label: 'In Game', icon: Gamepad2 },
    ...(lobbyIsHost === true
      ? [{ path: '/host', label: 'Host Panel', icon: Users }]
      : []),
  ];

  const itemClass = (path: string) =>
    `flex h-12 w-12 items-center justify-center rounded-control transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
      location.pathname === path
        ? 'bg-primary text-primary-foreground'
        : 'text-muted-foreground hover:bg-surface-2 hover:text-foreground'
    }`;

  return (
    <nav className="flex w-16 flex-col items-center gap-2 border-r border-border bg-surface py-4" aria-label="Main navigation">
      {navItems.map(item => (
        <button key={item.path}
          onClick={() => navigate(item.path)}
          title={item.label}
          aria-label={item.label}
          className={itemClass(item.path)}>
          <item.icon className="h-5 w-5" />
        </button>
      ))}
      {gameConnected && (
        <>
          <span className="mt-2 text-2xs font-semibold uppercase tracking-widest text-muted-foreground">
            Game
          </span>
          {gameItems.map(item => (
            <button key={item.path}
              onClick={() => navigate(item.path)}
              title={item.label}
              aria-label={item.label}
              className={itemClass(item.path)}>
              <item.icon className="h-5 w-5" />
            </button>
          ))}
        </>
      )}
      <div className="mt-auto flex flex-col items-center gap-2">
        {avatarUrl && (
          <img
            src={avatarUrl}
            alt={username}
            className="h-8 w-8 rounded-pill border border-border"
          />
        )}
        {username && (
          <span className="max-w-[56px] truncate text-xs text-muted-foreground">
            {username}
          </span>
        )}
      </div>
    </nav>
  );
}
