import { cn } from "@/lib/utils"

type SwitchProps = {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
};

export function Switch({ checked, onCheckedChange, disabled }: SwitchProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onCheckedChange(!checked)}
      className={cn(
        "flex h-6 w-11 shrink-0 items-center rounded-pill border transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
        checked ? "border-primary bg-primary" : "border-border bg-surface-2",
        disabled && "opacity-50"
      )}
    >
      <span className={cn(
        "block h-5 w-5 rounded-pill transition-transform",
        checked ? "translate-x-5 bg-background" : "translate-x-0.5 bg-muted-foreground"
      )} />
    </button>
  );
}

export type { SwitchProps }
