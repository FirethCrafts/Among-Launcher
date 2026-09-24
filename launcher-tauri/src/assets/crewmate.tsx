import type { SVGProps } from "react";

/**
 * Original, lightweight crewmate mark — a rounded body + visor + backpack.
 *
 * Hand-drawn geometry (not official key art), and deliberately drawn only in
 * the theme's neutral/primary tones so the crewmate player palette stays
 * reserved for player dots. Rendered as a component (not a `.svg` import)
 * because this project has no `vite-env.d.ts` module declaration for `*.svg`.
 */
export function CrewmateMark({ className, ...props }: SVGProps<SVGSVGElement>) {
  return (
    <svg
      viewBox="0 0 64 64"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      focusable="false"
      className={className}
      {...props}
    >
      {/* backpack */}
      <rect x="9" y="24" width="13" height="21" rx="6" fill="#c9c9d2" />
      {/* body */}
      <rect x="17" y="10" width="36" height="44" rx="17" fill="#ededf0" />
      {/* visor */}
      <rect x="28" y="17" width="20" height="13" rx="6.5" fill="#0a0a0c" />
      {/* visor highlight */}
      <rect
        x="32"
        y="20"
        width="7"
        height="4"
        rx="2"
        fill="#ffffff"
        fillOpacity="0.35"
      />
    </svg>
  );
}
