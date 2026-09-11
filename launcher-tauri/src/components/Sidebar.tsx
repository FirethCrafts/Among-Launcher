import { useNavigate, useLocation } from 'react-router-dom';
import { Home, Settings, Gamepad2, Users } from 'lucide-react';

const navItems = [
  { path: '/', label: 'Home', icon: Home },
  { path: '/settings', label: 'Settings', icon: Settings },
];

export function Sidebar({ gameConnected }: { gameConnected: boolean }) {
  const navigate = useNavigate();
  const location = useLocation();

  return (
    <nav className="w-16 flex flex-col items-center py-4 gap-2 bg-card border-r border-border">
      {navItems.map(item => (
        <button key={item.path}
          onClick={() => navigate(item.path)}
          className={`w-12 h-12 flex items-center justify-center rounded-lg transition-colors
            ${location.pathname === item.path
              ? 'bg-primary/10 text-primary'
              : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`}>
          <item.icon className="w-5 h-5" />
        </button>
      ))}
      {gameConnected && (
        <>
          <button onClick={() => navigate('/ingame')}
            className={`w-12 h-12 flex items-center justify-center rounded-lg transition-colors
              ${location.pathname === '/ingame'
                ? 'bg-primary/10 text-primary'
                : 'text-emerald-400 hover:bg-muted hover:text-emerald-300'}`}>
            <Gamepad2 className="w-5 h-5" />
          </button>
          <button onClick={() => navigate('/host')}
            className={`w-12 h-12 flex items-center justify-center rounded-lg transition-colors
              ${location.pathname === '/host'
                ? 'bg-primary/10 text-primary'
                : 'text-violet-400 hover:bg-muted hover:text-violet-300'}`}>
            <Users className="w-5 h-5" />
          </button>
        </>
      )}
    </nav>
  );
}
