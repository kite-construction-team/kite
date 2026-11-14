---
name: Obsidian Telemetry
colors:
  surface: '#131317'
  surface-dim: '#131317'
  surface-bright: '#39393d'
  surface-container-lowest: '#0e0e12'
  surface-container-low: '#1b1b1f'
  surface-container: '#1f1f23'
  surface-container-high: '#2a292e'
  surface-container-highest: '#353439'
  on-surface: '#e4e1e7'
  on-surface-variant: '#b9cacb'
  inverse-surface: '#e4e1e7'
  inverse-on-surface: '#303034'
  outline: '#849495'
  outline-variant: '#3a494b'
  surface-tint: '#00dce6'
  primary: '#e0fdff'
  on-primary: '#00373a'
  primary-container: '#00f2fe'
  on-primary-container: '#006a70'
  inverse-primary: '#00696f'
  secondary: '#dcb8ff'
  on-secondary: '#480081'
  secondary-container: '#7701d0'
  on-secondary-container: '#dcb7ff'
  tertiary: '#e1ffec'
  on-tertiary: '#003824'
  tertiary-container: '#67f4b7'
  on-tertiary-container: '#006e4b'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#6ff6ff'
  primary-fixed-dim: '#00dce6'
  on-primary-fixed: '#002022'
  on-primary-fixed-variant: '#004f53'
  secondary-fixed: '#efdbff'
  secondary-fixed-dim: '#dcb8ff'
  on-secondary-fixed: '#2c0051'
  on-secondary-fixed-variant: '#6700b5'
  tertiary-fixed: '#6ffbbe'
  tertiary-fixed-dim: '#4edea3'
  on-tertiary-fixed: '#002113'
  on-tertiary-fixed-variant: '#005236'
  background: '#131317'
  on-background: '#e4e1e7'
  surface-variant: '#353439'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 24px
    letterSpacing: -0.015em
  headline-sm:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '600'
    lineHeight: 20px
    letterSpacing: -0.01em
  body-lg:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 22px
    letterSpacing: 0em
  body-md:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
    letterSpacing: 0em
  body-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
    letterSpacing: 0.01em
  label-code-lg:
    fontFamily: JetBrains Mono
    fontSize: 13px
    fontWeight: '500'
    lineHeight: 18px
    letterSpacing: -0.01em
  label-code-md:
    fontFamily: JetBrains Mono
    fontSize: 11px
    fontWeight: '400'
    lineHeight: 16px
    letterSpacing: 0em
  label-code-sm:
    fontFamily: JetBrains Mono
    fontSize: 10px
    fontWeight: '500'
    lineHeight: 14px
    letterSpacing: 0.02em
  badge-label:
    fontFamily: JetBrains Mono
    fontSize: 9px
    fontWeight: '700'
    lineHeight: 12px
    letterSpacing: 0.06em
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  gutter: 0.75rem
  gutter-dense: 0.5rem
  margin: 1rem
  margin-window: 0.5rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1rem
  space-xl: 1.5rem
---

## Brand & Style

This design system is tailored for an elite, high-performance desktop administration environment managing mission-critical web server infrastructure (IIS re-engineered natively on Tauri). The aesthetic bridges deep void-black computing surfaces with ultra-precise luminescent telemetry, translating traditionally dense enterprise tooling into an authoritative, hyper-focused cockpit.

### Visual Aesthetic & Philosophy
- **Deep Obsidian Architecture:** Built entirely upon absolute blacks and tiered charcoal-carbon tones. The UI recedes into the hardware bezel, letting dynamic server metrics and application trees project forward.
- **Luminescent Precision:** Neon hairline accents provide instant sensory feedback. Visual weight is communicated through glowing photons, crisp 1px laser-etched borders, and faint optical bloom rather than heavy fills.
- **Glassmorphic Substratum:** Fine backdrop blurs (16px–24px) with high-falloff scrims provide visual stratification across elevated panels, inspector flyouts, and command palettes without losing structural grounding.
- **Desktop Native Tactility:** Tailored for ultra-responsive low-latency desktop execution with dedicated native Tauri window controls, dense data grids, responsive split-panes, and high-frequency telemetry gauges.

## Colors

The color architecture is built around an optical hierarchy of dark matter and controlled photon emissions. Surfaces rely on pure near-blacks to reduce eye strain over prolonged operational sessions, accented by laser-calibrated status spectrums.

### Core Surface Roles
- **Base Canvas (`#0a0a0c`):** The primary root application background. Zero ambient reflectance.
- **Layer 1 Substratum (`#111115`):** Side navigation, inspector drawers, and window toolbars.
- **Layer 2 Container (`#16161d`):** Data tables, cards, telemetry cards, and nested group panels.
- **Layer 3 Active Surface (`#1e1e27`):** Hovered rows, floating command palettes, and active tabs.

### Luminescent Accents
- **Primary Electric Cyan (`#00f2fe` to `#4facfe`):** Active connections, binding protocols (HTTP/HTTPS), primary system calls, focused input perimeters.
- **Secondary Vivid Violet / Magenta (`#8a2be2` to `#f72585`):** Worker processes, App Pool recycling states, SSL/TLS certificate governance, routing pipelines.
- **Telemetry Emerald (`#10b981`):** Operational health, active bindings, nominal uptime, clean telemetry responses (HTTP 200–2xx).
- **Telemetry Amber (`#f59e0b`):** App pool throttling, latency anomalies, queue bottlenecks, HTTP 4xx warnings.
- **Telemetry Crimson (`#ef4444`):** Stopped sites, process crashes, fatal 5xx errors, unhandled ISAPI/FastCGI exceptions.

### Structural Hairlines
- **Hairline Border Base:** `rgba(255, 255, 255, 0.08)` to `rgba(255, 255, 255, 0.12)`. No solid opaque gray borders.
- **Hairline Glow Accent:** `rgba(0, 242, 254, 0.35)` with an optical 4px drop blur on focus or critical selection.

## Typography

The typographic system utilizes a dual-engine structure: **Inter** handles narrative scanability, layout headers, and settings hierarchy, while **JetBrains Mono** governs technical readouts, telemetry metrics, paths, IP sockets, and status badges.

### Typographic Hierarchy Guidelines
- **Mono Data Integrity:** All numerical values, process IDs (PID), memory allotments (MB/GB), IP addresses, and HTTP status codes must render in `JetBrains Mono`. Ensure tabular figures (`tnum`) are enabled to prevent horizontal jitter during rapid telemetry streams.
- **Subdued Lettering:** Body copy defaults to high-clarity muted tones (`rgba(255, 255, 255, 0.72)`) to keep luminous neon indicators as the highest-contrast visual focal point.
- **Micro Badging:** `badge-label` must always be transformed to `uppercase` with tracking expanded to `0.06em` to maintain legibility at 9px within status chips and tag pills.

## Layout & Spacing

Designed for high-density multi-display sysadmin environments, the layout rejects bloated consumer whitespace in favor of rigorous, information-dense operational docks.

### Desktop Layout Engine
- **Master Shell Composition:** Multi-pane workspace featuring an integrated custom Tauri title bar (`36px` height), collapsible site/pool hierarchical explorer (`260px` to `340px`), central telemetry canvas, and right-hand inspector/action flyout (`280px`).
- **Data Density Modes:**
  - **Dense (Standard Dashboard):** `space-xs` (4px) to `space-sm` (8px) internal cell paddings. Optimized for 100+ concurrent worker processes and deep module lists.
  - **Inspection (Detail Views):** `space-md` (12px) to `space-lg` (16px) margins for log tails and certificate inspection diagrams.
- **Window Padding:** Native Tauri app boundary features a strict `margin-window` (8px) padding offset when windowed, collapsing to `0px` when maximized.
- **Panel Splitters:** 1px hairline dividers with a zero-width interactive hit area of 6px to allow effortless resize manipulation of treeviews and log drawers.

## Elevation & Depth

Rather than conventional drop shadows, this design system communicates depth through dark tonal stratification, hairline border luminosity, and glassmorphism.

### Depth Hierarchy
- **Level 0 (Canvas Base):** Solid `#0a0a0c`. Root viewport hosting inactive workspaces.
- **Level 1 (Docked Structural Toolbars):** Background `#111115` with `1px border` (`rgba(255, 255, 255, 0.07)`). Non-scrolling structural frames.
- **Level 2 (Data Cards & Workstations):** Background `rgba(22, 22, 29, 0.75)` with `backdrop-filter: blur(16px)` and perimeter border `rgba(255, 255, 255, 0.1)`.
- **Level 3 (Floating Overlays & Modals):** Background `rgba(26, 26, 35, 0.88)` with `backdrop-filter: blur(24px)`, perimeter border `rgba(255, 255, 255, 0.18)`, and a subtle ambient occlusion glow: `0 8px 32px rgba(0, 0, 0, 0.8)`.

### Luminescent Laser Edges
Focused elements, active server states, and dragged split-panes drop traditional drop shadows in favor of a 1px perimeter glow:
- `box-shadow: 0 0 12px -2px rgba(0, 242, 254, 0.25), inset 0 0 0 1px rgba(0, 242, 254, 0.6)`.

## Shapes

The design system enforces architectural sharpness with low roundedness (`roundedness: 1` — Soft, base `0.25rem` / 4px). This matches the precision expected of low-level systems software and optimizes screen real-estate.

### Shape Distribution
- **Cards, Panels, Inputs, Data Grids:** `4px` (`rounded-sm`). Keeps table corners tight and maximize usable data width.
- **Badges, Status Chips, Micro Pills:** `2px` to `4px`. Crisp, tag-like form factors that mimic hardware faceplates.
- **Tauri Window Frame:** `8px` outer radius when running in windowed desktop mode on macOS/Windows 11, strictly squared (`0px`) on continuous tiled displays or when snapped.
- **Interactive Modals:** `8px` (`rounded-lg`) to visually disconnect elevated overlays from the underlying rectangular grid.

## Components

### 1. Tauri Window Title Bar & Native Controls
- **Shell Toolbar:** Height `36px`, background `#0a0a0c`, drag region enabled (`data-tauri-drag-region`).
- **Window Controls (Close, Min, Max):** Integrated directly into the top right (or top left on macOS). Close hover states trigger a clean crimson background (`#ef4444`) with instant opacity transition.
- **Global Health Micro-Bar:** Embedded in the window title line showing instantaneous CPU %, memory pool consumption, and active requests per second in `label-code-sm`.

### 2. Buttons
- **Primary Cybernetic Button:** Background `rgba(0, 242, 254, 0.12)`, border `1px solid #00f2fe`, text `#00f2fe`, font `Inter 500`. Hover produces ambient inner/outer glow (`box-shadow: 0 0 10px rgba(0, 242, 254, 0.3)`).
- **Secondary / Ghost Button:** Background `rgba(255, 255, 255, 0.04)`, border `1px solid rgba(255, 255, 255, 0.08)`, text `rgba(255, 255, 255, 0.85)`. Hover transitions border to `rgba(255, 255, 255, 0.2)`.
- **Destructive Action (Stop / Kill Process):** Background `rgba(239, 68, 68, 0.1)`, border `1px solid rgba(239, 68, 68, 0.4)`, text `#ef4444`.

### 3. Telemetry Indicators & Status Pills
- **State Indicator:** 6px circular dot equipped with a dual concentric ring: solid center dot paired with a pulsing radial glow (`box-shadow: 0 0 6px currentColor`).
  - Active: `#10b981` (Emerald)
  - Recycling/Starting: `#00f2fe` (Cyan)
  - Warning/Degraded: `#f59e0b` (Amber)
  - Terminated: `#ef4444` (Crimson)
- **Binding Chips:** Background `rgba(255, 255, 255, 0.03)`, border `1px solid rgba(255, 255, 255, 0.08)`, text in `JetBrains Mono` (`label-code-sm`). Protocols (`http`, `https`, `net.tcp`) highlighted via cyan or magenta tint.

### 4. Input Fields & Search Bars
- **Style:** Background `#0a0a0c`, border `1px solid rgba(255, 255, 255, 0.1)`, text `rgba(255, 255, 255, 0.9)`, font `JetBrains Mono` (`label-code-md`).
- **Focus State:** 1px hairline border changes to `#00f2fe` with an accent perimeter aura (`0 0 0 1px #00f2fe`).
- **Command Filter (`Ctrl+K` / `Cmd+K`):** Inline keyboard shortcut badge right-aligned within the input field using `rgba(255, 255, 255, 0.08)` surface.

### 5. Data Tables (Application Pools & Sites)
- **Container:** Dark surface `#111115` bounded by hairline `rgba(255, 255, 255, 0.08)`.
- **Header:** Sticky, `28px` height, uppercase `badge-label`, background `#16161d`, text `rgba(255, 255, 255, 0.5)`.
- **Row Styling:** Height `32px`, alternate striping avoided. Separated by `1px solid rgba(255, 255, 255, 0.04)`. Hover highlights row with `#1a1a24` and displays actionable quick-controls (Recycle, Explore, Stop).
- **Selection:** Selected row displays an active 2px vertical neon-cyan indicator anchored to the absolute left edge.

### 6. Cards & Telemetry Nodes
- **Surface:** `rgba(22, 22, 29, 0.7)` with `backdrop-filter: blur(12px)`.
- **Header:** Integrated action menu with hairline bottom divider.
- **Metric Content:** Large numerical telemetry figures rendered in `JetBrains Mono` (`headline-lg`) paired with a micro sparkline rendered in vibrant neon strokes without fill.