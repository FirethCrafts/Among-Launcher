import { useNavigate, useLocation } from 'react-router-dom';
import { Home, Settings, Gamepad2, Users } from 'lucide-react';

const navItems = [
  { path: '/', label: 'Home', icon: Home },
  { path: '/settings', label: 'Settings', icon: Settings },
];

const gameItems = [
  { path: '/ingame', label: 'In Game', icon: Gamepad2 },
  { path: '/host', label: 'Host Panel', icon: Users },
];

interface SidebarProps {
  gameConnected: boolean;
  username: string;
  avatarUrl: string;
}

export function Sidebar({ gameConnected, username, avatarUrl }: SidebarProps) {
  const navigate = useNavigate();
  const location = useLocation();

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
