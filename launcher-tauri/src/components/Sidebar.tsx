import { useNavigate, useLocation } from 'react-router-dom';
import { Home, Archive, Settings, Gamepad2, Users, ChevronsLeft, ChevronsRight } from 'lucide-react';
import { NavItem } from '@/components/ui/nav-item';
import { cn } from '@/lib/utils';

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
  /** `true` renders the 64px icon rail; `false` (default) shows icon + label. */
  collapsed?: boolean;
  onToggleCollapsed?: () => void;
}

export function Sidebar({
  gameConnected,
  username,
  avatarUrl,
  lobbyIsHost,
  collapsed = false,
  onToggleCollapsed,
}: SidebarProps) {
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

  const isActive = (path: string) => location.pathname === path;

  return (
    <nav
      className={cn(
        'flex flex-col gap-2 border-r border-border bg-surface py-4',
        collapsed ? 'w-16 items-center' : 'w-[200px] px-3'
      )}
      aria-label="Main navigation"
    >
      <div className={cn('flex flex-col gap-1', collapsed ? 'items-center' : 'w-full')}>
        {navItems.map(item => (
          <NavItem
            key={item.path}
            icon={item.icon}
            label={item.label}
            active={isActive(item.path)}
            collapsed={collapsed}
            onClick={() => navigate(item.path)}
          />
        ))}
      </div>

      {gameConnected && (
        <div className={cn('flex flex-col gap-1', collapsed ? 'items-center' : 'w-full')}>
          {collapsed ? (
            <span className="mt-2 h-px w-8 bg-border" aria-hidden="true" />
          ) : (
            <span className="mt-2 px-3 text-2xs font-semibold uppercase tracking-widest text-muted-foreground">
              Game
            </span>
          )}
          {gameItems.map(item => (
            <NavItem
              key={item.path}
              icon={item.icon}
              label={item.label}
              active={isActive(item.path)}
              collapsed={collapsed}
              onClick={() => navigate(item.path)}
            />
          ))}
        </div>
      )}

      <div className={cn('mt-auto flex flex-col gap-2', collapsed ? 'items-center' : 'w-full')}>
        {collapsed ? (
          avatarUrl && (
            <img
              src={avatarUrl}
              alt={username}
              title={username}
              className="h-8 w-8 rounded-pill border border-border"
            />
          )
        ) : (
          (avatarUrl || username) && (
            <div className="flex min-w-0 items-center gap-2 px-3">
              {avatarUrl && (
                <img
                  src={avatarUrl}
                  alt={username}
                  className="h-8 w-8 shrink-0 rounded-pill border border-border"
                />
              )}
              {username && (
                <span className="truncate text-sm text-muted-foreground">{username}</span>
              )}
            </div>
          )
        )}

        {onToggleCollapsed && (
          <button
            type="button"
            onClick={onToggleCollapsed}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            className={cn(
              'flex items-center justify-center rounded-control text-muted-foreground transition-colors hover:bg-surface-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
              collapsed ? 'h-9 w-9' : 'h-9 w-full gap-2 px-3'
            )}
          >
            {collapsed ? (
              <ChevronsRight className="h-4 w-4 shrink-0" />
            ) : (
              <>
                <ChevronsLeft className="h-4 w-4 shrink-0" />
                <span className="text-13">Collapse</span>
              </>
            )}
          </button>
        )}
      </div>
    </nav>
  );
}
