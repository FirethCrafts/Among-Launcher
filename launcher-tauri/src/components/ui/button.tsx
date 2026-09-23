import * as React from "react"
import { cva } from "class-variance-authority"
import { cn } from "@/lib/utils"

type ButtonProps = {
  /** `primary` is an alias of `default`; `danger` is an alias of `destructive`. */
  variant?: 'default' | 'primary' | 'outline' | 'secondary' | 'ghost' | 'destructive' | 'danger';
  size?: 'sm' | 'md' | 'lg' | 'icon';
};

const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-control text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary-hover",
        primary: "bg-primary text-primary-foreground hover:bg-primary-hover",
        secondary: "border border-border bg-surface-2 text-foreground hover:bg-border",
        outline: "border border-border bg-transparent text-foreground hover:bg-surface-2",
        ghost: "text-muted-foreground hover:bg-surface-2 hover:text-foreground",
        destructive: "bg-destructive text-destructive-foreground hover:bg-destructive/90",
        danger: "bg-danger text-white hover:bg-danger/90",
      },
      size: {
        sm: "h-8 rounded-control px-3 text-xs",
        md: "h-9 px-4 py-2",
        lg: "h-10 rounded-control px-6",
        icon: "h-9 w-9",
      },
    },
    defaultVariants: { variant: "default", size: "md" },
  }
)

export interface ButtonComponentProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    ButtonProps {}

const Button = React.forwardRef<HTMLButtonElement, ButtonComponentProps>(
  ({ className, variant, size, type = "button", ...props }, ref) => (
    <button
      ref={ref}
      type={type}
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  )
)
Button.displayName = "Button"

export { Button, buttonVariants }
export type { ButtonProps }
