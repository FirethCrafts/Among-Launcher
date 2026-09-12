import * as React from "react"
import { cva } from "class-variance-authority"
import { cn } from "@/lib/utils"

type ButtonProps = {
  variant?: 'default' | 'outline' | 'secondary' | 'ghost' | 'destructive';
  size?: 'sm' | 'md' | 'lg' | 'icon';
};

const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default: "border border-white/10 bg-primary text-primary-foreground shadow hover:bg-primary/90",
        outline: "border border-white/10 bg-white/5 shadow-sm backdrop-blur-md hover:bg-white/10 hover:text-accent-foreground",
        secondary: "border border-white/10 bg-white/10 text-secondary-foreground shadow-sm backdrop-blur-md hover:bg-white/15",
        ghost: "hover:bg-muted/60 hover:text-accent-foreground",
        destructive: "border border-white/10 bg-destructive text-white shadow-sm hover:bg-destructive/90",
      },
      size: {
        sm: "h-8 rounded-md px-3 text-xs",
        md: "h-9 px-4 py-2",
        lg: "h-10 rounded-md px-6",
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
