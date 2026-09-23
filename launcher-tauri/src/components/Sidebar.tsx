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

  return (
    <nav className="w-16 flex flex-col items-center py-4 gap-2 bg-card border-r border-border" aria-label="Main navigation">
      {navItems.map(item => (
        <button key={item.path}
          onClick={() => navigate(item.path)}
          title={item.label}
          aria-label={item.label}
          className={`w-12 h-12 flex items-center justify-center rounded-lg transition-colors
            ${location.pathname === item.path
              ? 'bg-primary/15 text-primary'
              : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`}>
          <item.icon className="w-5 h-5" />
        </button>
      ))}
      {gameConnected && (
        <>
          <span className="mt-2 text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">
            Game
          </span>
          {gameItems.map(item => (
            <button key={item.path}
              onClick={() => navigate(item.path)}
              title={item.label}
              aria-label={item.label}
              className={`w-12 h-12 flex items-center justify-center rounded-lg transition-colors
                ${location.pathname === item.path
                  ? 'bg-primary/15 text-primary'
                  : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`}>
              <item.icon className="w-5 h-5" />
            </button>
          ))}
        </>
      )}
      <div className="mt-auto flex flex-col items-center gap-2">
        {avatarUrl && (
          <img
            src={avatarUrl}
            alt={username}
            className="w-8 h-8 rounded-full"
          />
        )}
        {username && (
          <span className="text-xs text-muted-foreground truncate max-w-[56px]">
            {username}
          </span>
        )}
      </div>
    </nav>
  );
}
