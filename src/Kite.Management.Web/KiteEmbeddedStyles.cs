namespace Kite.Management.Web;

/// <summary>
/// Pre-compiled, zero-network-dependency CSS implementing the "Obsidian Telemetry" Design System
/// (<c>design/DesktopWinForms/obsidian_telemetry/DESIGN.md</c>).
/// Eliminates runtime Tailwind CDN compilation and external webfont blocking while providing
/// deep obsidian surfaces, luminescent cyan/violet/emerald accents, and tabular monospace telemetry.
/// </summary>
public static class KiteEmbeddedStyles
{
    public const string TailwindCss = """
        :root {
            --surface: #131317;
            --surface-dim: #131317;
            --surface-bright: #39393d;
            --surface-container-lowest: #0e0e12;
            --surface-container-low: #1b1b1f;
            --surface-container: #1f1f23;
            --surface-container-high: #2a292e;
            --surface-container-highest: #353439;
            --on-surface: #e4e1e7;
            --on-surface-variant: #b9cacb;
            --outline: #849495;
            --outline-variant: #3a494b;
            --primary: #e0fdff;
            --on-primary: #00373a;
            --primary-container: #00f2fe;
            --on-primary-container: #006a70;
            --primary-fixed: #6ff6ff;
            --primary-fixed-dim: #00dce6;
            --secondary: #dcb8ff;
            --on-secondary: #480081;
            --secondary-container: #7701d0;
            --on-secondary-container: #dcb7ff;
            --secondary-fixed: #efdbff;
            --secondary-fixed-dim: #dcb8ff;
            --tertiary: #e1ffec;
            --on-tertiary: #003824;
            --tertiary-container: #67f4b7;
            --on-tertiary-container: #006e4b;
            --tertiary-fixed: #6ffbbe;
            --tertiary-fixed-dim: #4edea3;
            --error: #ffb4ab;
            --on-error: #690005;
            --error-container: #93000a;
            --on-error-container: #ffdad6;
            --font-sans: 'Inter', 'Segoe UI Variable Display', 'Segoe UI', system-ui, -apple-system, sans-serif;
            --font-mono: 'JetBrains Mono', 'Cascadia Mono', 'Cascadia Code', Consolas, monospace;
        }
        *, ::before, ::after { box-sizing: border-box; border-width: 0; border-style: solid; border-color: rgba(255,255,255,0.08); }
        html, body {
            margin: 0; padding: 0;
            background-color: #0e0e12;
            color: #e4e1e7;
            font-family: var(--font-sans);
            font-size: 13px;
            line-height: 18px;
            overscroll-behavior: none;
            -webkit-font-smoothing: antialiased;
        }
        ::-webkit-scrollbar { width: 6px; height: 6px; }
        ::-webkit-scrollbar-track { background: #0e0e12; }
        ::-webkit-scrollbar-thumb { background: #2a292e; border-radius: 3px; }
        ::-webkit-scrollbar-thumb:hover { background: #00f2fe; }
        a { color: inherit; text-decoration: none; }
        button, input, select { font-family: inherit; }
        button { cursor: pointer; background: transparent; color: inherit; }

        /* Typography Hierarchy from DESIGN.md */
        .font-headline-xl, .text-headline-xl { font-family: var(--font-sans); font-size: 24px; font-weight: 600; line-height: 32px; letter-spacing: -0.02em; }
        .font-headline-lg, .text-headline-lg { font-family: var(--font-sans); font-size: 18px; font-weight: 600; line-height: 24px; letter-spacing: -0.015em; }
        .font-headline-sm, .text-headline-sm { font-family: var(--font-sans); font-size: 14px; font-weight: 600; line-height: 20px; letter-spacing: -0.01em; }
        .font-body-lg, .text-body-lg { font-family: var(--font-sans); font-size: 14px; font-weight: 400; line-height: 22px; }
        .font-body-md, .text-body-md { font-family: var(--font-sans); font-size: 13px; font-weight: 400; line-height: 18px; }
        .font-body-sm, .text-body-sm { font-family: var(--font-sans); font-size: 12px; font-weight: 400; line-height: 16px; letter-spacing: 0.01em; }
        .font-label-code-lg, .text-label-code-lg { font-family: var(--font-mono); font-size: 13px; font-weight: 500; line-height: 18px; letter-spacing: -0.01em; font-variant-numeric: tabular-nums; }
        .font-label-code-md, .text-label-code-md { font-family: var(--font-mono); font-size: 11px; font-weight: 400; line-height: 16px; font-variant-numeric: tabular-nums; }
        .font-label-code-sm, .text-label-code-sm { font-family: var(--font-mono); font-size: 10px; font-weight: 500; line-height: 14px; letter-spacing: 0.02em; font-variant-numeric: tabular-nums; }
        .font-badge-label, .text-badge-label { font-family: var(--font-mono); font-size: 9px; font-weight: 700; line-height: 12px; letter-spacing: 0.06em; text-transform: uppercase; font-variant-numeric: tabular-nums; }
        .font-mono { font-family: var(--font-mono); font-variant-numeric: tabular-nums; }

        /* Surface & Accent Colors */
        .bg-background, .bg-surface { background-color: #131317; }
        .bg-surface-container-lowest { background-color: #0e0e12; }
        .bg-surface-container-lowest\/90 { background-color: rgba(14, 14, 18, 0.92); }
        .bg-surface-container-lowest\/95 { background-color: rgba(14, 14, 18, 0.96); }
        .bg-surface-container-lowest\/80 { background-color: rgba(14, 14, 18, 0.82); }
        .bg-surface-container-lowest\/60 { background-color: rgba(14, 14, 18, 0.65); }
        .bg-surface-container-lowest\/40 { background-color: rgba(14, 14, 18, 0.45); }
        .bg-surface-container-low { background-color: #1b1b1f; }
        .bg-surface-container-low\/95 { background-color: rgba(27, 27, 31, 0.96); }
        .bg-surface-container-low\/90 { background-color: rgba(27, 27, 31, 0.92); }
        .bg-surface-container-low\/80 { background-color: rgba(27, 27, 31, 0.84); }
        .bg-surface-container { background-color: #1f1f23; }
        .bg-surface-container\/70 { background-color: rgba(31, 31, 35, 0.78); }
        .bg-surface-container-high { background-color: #2a292e; }
        .bg-surface-container-high\/60 { background-color: rgba(42, 41, 46, 0.65); }
        .bg-surface-container-high\/40 { background-color: rgba(42, 41, 46, 0.45); }
        .bg-surface-container-high\/30 { background-color: rgba(42, 41, 46, 0.32); }
        .bg-surface-container-highest { background-color: #353439; }
        .bg-surface-container-highest\/90 { background-color: rgba(53, 52, 57, 0.92); }
        .bg-surface-variant { background-color: #353439; }
        .bg-primary { background-color: #e0fdff; }
        .bg-primary\/10 { background-color: rgba(224, 253, 255, 0.10); }
        .bg-primary-container { background-color: #00f2fe; }
        .bg-primary-container\/20 { background-color: rgba(0, 242, 254, 0.20); }
        .bg-primary-container\/15 { background-color: rgba(0, 242, 254, 0.15); }
        .bg-primary-container\/10 { background-color: rgba(0, 242, 254, 0.10); }
        .bg-secondary { background-color: #dcb8ff; }
        .bg-secondary\/10 { background-color: rgba(220, 184, 255, 0.10); }
        .bg-secondary-container { background-color: #7701d0; }
        .bg-secondary-container\/40 { background-color: rgba(119, 1, 208, 0.40); }
        .bg-secondary-container\/20 { background-color: rgba(119, 1, 208, 0.22); }
        .bg-tertiary { background-color: #e1ffec; }
        .bg-tertiary\/10 { background-color: rgba(103, 244, 183, 0.12); }
        .bg-tertiary-container { background-color: #67f4b7; }
        .bg-tertiary-container\/20 { background-color: rgba(103, 244, 183, 0.20); }
        .bg-error { background-color: #ffb4ab; }
        .bg-error-container { background-color: #93000a; }
        .bg-error-container\/20 { background-color: rgba(147, 0, 10, 0.25); }
        .bg-error-container\/10 { background-color: rgba(147, 0, 10, 0.14); }

        .text-on-surface { color: #e4e1e7; }
        .text-on-surface-variant { color: #b9cacb; }
        .text-on-surface-variant\/70 { color: rgba(185, 202, 203, 0.70); }
        .text-on-surface-variant\/50 { color: rgba(185, 202, 203, 0.50); }
        .text-on-surface-variant\/40 { color: rgba(185, 202, 203, 0.40); }
        .text-primary { color: #e0fdff; }
        .text-on-primary { color: #00373a; }
        .text-primary-container { color: #00f2fe; }
        .text-on-primary-container { color: #002022; }
        .text-primary-fixed { color: #6ff6ff; }
        .text-primary-fixed-dim { color: #00dce6; }
        .text-secondary { color: #dcb8ff; }
        .text-on-secondary-container { color: #dcb7ff; }
        .text-secondary-fixed { color: #efdbff; }
        .text-secondary-fixed-dim { color: #dcb8ff; }
        .text-tertiary { color: #e1ffec; }
        .text-tertiary-container { color: #67f4b7; }
        .text-tertiary-fixed-dim { color: #4edea3; }
        .text-error { color: #ffb4ab; }
        .text-on-error-container { color: #ffdad6; }

        /* Hover States */
        .hover\:bg-surface-container:hover { background-color: #1f1f23; }
        .hover\:bg-surface-container-high:hover { background-color: #2a292e; }
        .hover\:bg-surface-container-highest:hover { background-color: #353439; }
        .hover\:bg-primary-container\/25:hover { background-color: rgba(0, 242, 254, 0.25); }
        .hover\:bg-error-container:hover { background-color: #93000a; }
        .hover\:text-on-surface:hover { color: #e4e1e7; }
        .hover\:text-primary:hover { color: #e0fdff; }
        .hover\:text-primary-container:hover { color: #00f2fe; }
        .hover\:text-on-error-container:hover { color: #ffdad6; }
        .hover\:underline:hover { text-decoration: underline; }

        /* Layout & Shell Positioning */
        .fixed { position: fixed; }
        .relative { position: relative; }
        .absolute { position: absolute; }
        .sticky { position: sticky; }
        .inset-0 { top: 0; right: 0; bottom: 0; left: 0; }
        .top-0 { top: 0; }
        .bottom-0 { bottom: 0; }
        .left-0 { left: 0; }
        .right-0 { right: 0; }
        .top-14 { top: 3.5rem; }
        .bottom-8 { bottom: 2rem; }
        .z-10 { z-index: 10; }
        .z-40 { z-index: 40; }
        .z-50 { z-index: 50; }
        .h-14 { height: 3.5rem; }
        .h-10 { height: 2.5rem; }
        .h-8 { height: 2rem; }
        .h-6 { height: 1.5rem; }
        .h-5 { height: 1.25rem; }
        .h-4 { height: 1rem; }
        .h-3 { height: 0.75rem; }
        .h-2 { height: 0.5rem; }
        .h-1\.5 { height: 0.375rem; }
        .h-px { height: 1px; }
        .h-44 { height: 11rem; }
        .h-56 { height: 14rem; }
        .w-72 { width: 18rem; }
        .w-48 { width: 12rem; }
        .w-36 { width: 9rem; }
        .w-28 { width: 7rem; }
        .w-16 { width: 4rem; }
        .w-10 { width: 2.5rem; }
        .w-8 { width: 2rem; }
        .w-2 { width: 0.5rem; }
        .w-1\.5 { width: 0.375rem; }
        .w-px { width: 1px; }
        .w-full { width: 100%; }
        .min-h-screen { min-height: 100vh; }
        .min-w-0 { min-width: 0; }
        .max-w-md { max-width: 28rem; }
        .max-w-lg { max-width: 32rem; }
        .max-w-xl { max-width: 36rem; }
        .max-w-2xl { max-width: 42rem; }
        .max-w-3xl { max-width: 48rem; }
        .max-w-xs { max-width: 20rem; }
        .w-inspector { width: 420px; }
        .max-h-96 { max-height: 24rem; }
        .max-h-64 { max-height: 16rem; }
        .max-h-36 { max-height: 9rem; }
        .pl-72 { padding-left: 18rem; }
        .pt-14 { padding-top: 3.5rem; }
        .pb-8 { padding-bottom: 2rem; }
        .pb-10 { padding-bottom: 2.5rem; }

        /* Spacing Tokens from DESIGN.md */
        .p-0\.5 { padding: 0.125rem; }
        .p-1 { padding: 0.25rem; }
        .p-1\.5 { padding: 0.375rem; }
        .p-2 { padding: 0.5rem; }
        .p-space-xs { padding: 0.25rem; }
        .p-space-sm { padding: 0.5rem; }
        .p-space-md { padding: 0.75rem; }
        .p-space-lg { padding: 1rem; }
        .px-1 { padding-left: 0.25rem; padding-right: 0.25rem; }
        .px-1\.5 { padding-left: 0.375rem; padding-right: 0.375rem; }
        .px-2 { padding-left: 0.5rem; padding-right: 0.5rem; }
        .px-2\.5 { padding-left: 0.625rem; padding-right: 0.625rem; }
        .px-3 { padding-left: 0.75rem; padding-right: 0.75rem; }
        .px-3\.5 { padding-left: 0.875rem; padding-right: 0.875rem; }
        .px-4 { padding-left: 1rem; padding-right: 1rem; }
        .px-8 { padding-left: 2rem; padding-right: 2rem; }
        .px-space-xs { padding-left: 0.25rem; padding-right: 0.25rem; }
        .px-space-sm { padding-left: 0.5rem; padding-right: 0.5rem; }
        .px-space-md { padding-left: 0.75rem; padding-right: 0.75rem; }
        .px-space-lg { padding-left: 1rem; padding-right: 1rem; }
        .py-0\.5 { padding-top: 0.125rem; padding-bottom: 0.125rem; }
        .py-1 { padding-top: 0.25rem; padding-bottom: 0.25rem; }
        .py-1\.5 { padding-top: 0.375rem; padding-bottom: 0.375rem; }
        .py-2 { padding-top: 0.5rem; padding-bottom: 0.5rem; }
        .py-2\.5 { padding-top: 0.625rem; padding-bottom: 0.625rem; }
        .pt-1 { padding-top: 0.25rem; }
        .pt-2 { padding-top: 0.5rem; }
        .pt-space-xs { padding-top: 0.25rem; }
        .pb-1 { padding-bottom: 0.25rem; }
        .pb-2 { padding-bottom: 0.5rem; }
        .pb-4 { padding-bottom: 1rem; }
        .pb-space-xs { padding-bottom: 0.25rem; }
        .pb-space-sm { padding-bottom: 0.5rem; }
        .pl-8 { padding-left: 2rem; }
        .pr-3 { padding-right: 0.75rem; }
        .pr-6 { padding-right: 1.5rem; }
        .pr-12 { padding-right: 3rem; }
        .pr-16 { padding-right: 4rem; }
        .m-space-xs { margin: 0.25rem; }
        .mx-1 { margin-left: 0.25rem; margin-right: 0.25rem; }
        .mx-space-xl { margin-left: 1.5rem; margin-right: 1.5rem; }
        .my-1 { margin-top: 0.25rem; margin-bottom: 0.25rem; }
        .my-space-xs { margin-top: 0.25rem; margin-bottom: 0.25rem; }
        .my-space-sm { margin-top: 0.5rem; margin-bottom: 0.5rem; }
        .mt-0\.5 { margin-top: 0.125rem; }
        .mt-1 { margin-top: 0.25rem; }
        .mt-2 { margin-top: 0.5rem; }
        .mt-space-md { margin-top: 0.75rem; }
        .mb-1 { margin-bottom: 0.25rem; }
        .mb-space-xs { margin-bottom: 0.25rem; }
        .mb-space-sm { margin-bottom: 0.5rem; }
        .mb-space-md { margin-bottom: 0.75rem; }
        .mb-space-lg { margin-bottom: 1rem; }
        .mb-space-xl { margin-bottom: 1.5rem; }
        .ml-2 { margin-left: 0.5rem; }
        .ml-space-xs { margin-left: 0.25rem; }
        .mr-1\.5 { margin-right: 0.375rem; }

        /* Flexbox & Grid */
        .flex { display: flex; }
        .inline-flex { display: inline-flex; }
        .grid { display: grid; }
        .hidden { display: none; }
        .flex-col { flex-direction: column; }
        .flex-wrap { flex-wrap: wrap; }
        .flex-1 { flex: 1 1 0%; }
        .shrink-0 { flex-shrink: 0; }
        .items-center { align-items: center; }
        .items-start { align-items: flex-start; }
        .items-baseline { align-items: baseline; }
        .justify-between { justify-content: space-between; }
        .justify-center { justify-content: center; }
        .justify-end { justify-content: flex-end; }
        .gap-0\.5 { gap: 0.125rem; }
        .gap-1 { gap: 0.25rem; }
        .gap-1\.5 { gap: 0.375rem; }
        .gap-2 { gap: 0.5rem; }
        .gap-3 { gap: 0.75rem; }
        .gap-space-xs { gap: 0.25rem; }
        .gap-space-sm { gap: 0.5rem; }
        .gap-space-md { gap: 0.75rem; }
        .gap-space-lg { gap: 1rem; }
        .grid-cols-1 { grid-template-columns: repeat(1, minmax(0, 1fr)); }
        .grid-cols-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
        .grid-cols-3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }
        .grid-cols-4 { grid-template-columns: repeat(4, minmax(0, 1fr)); }
        .grid-cols-8 { grid-template-columns: repeat(8, minmax(0, 1fr)); }
        @media (min-width: 640px) {
            .sm\:grid-cols-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
            .sm\:flex { display: flex; }
        }
        @media (min-width: 768px) {
            .md\:flex-row { flex-direction: row; }
            .md\:items-center { align-items: center; }
            .md\:grid-cols-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
            .md\:grid-cols-3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }
            .md\:grid-cols-4 { grid-template-columns: repeat(4, minmax(0, 1fr)); }
        }
        @media (min-width: 1024px) {
            .lg\:grid-cols-4 { grid-template-columns: repeat(4, minmax(0, 1fr)); }
            .lg\:grid-cols-12 { grid-template-columns: repeat(12, minmax(0, 1fr)); }
            .lg\:col-span-8 { grid-column: span 8 / span 8; }
            .lg\:col-span-7 { grid-column: span 7 / span 7; }
            .lg\:col-span-6 { grid-column: span 6 / span 6; }
            .lg\:col-span-5 { grid-column: span 5 / span 5; }
            .lg\:col-span-4 { grid-column: span 4 / span 4; }
        }
        @media (min-width: 1280px) {
            .xl\:grid-cols-4 { grid-template-columns: repeat(4, minmax(0, 1fr)); }
        }

        /* Borders, Shapes & Luminescent Elevations */
        .rounded-sm { border-radius: 0.125rem; }
        .rounded { border-radius: 0.25rem; }
        .rounded-lg { border-radius: 0.375rem; }
        .rounded-xl { border-radius: 0.5rem; }
        .rounded-full { border-radius: 9999px; }
        .border { border-width: 1px; }
        .border-b { border-bottom-width: 1px; }
        .border-t { border-top-width: 1px; }
        .border-l { border-left-width: 1px; }
        .border-surface-variant { border-color: rgba(255, 255, 255, 0.08); }
        .obsidian-card {
            background-color: #1b1b1f;
            border: 1px solid rgba(255, 255, 255, 0.08);
        }
        .obsidian-glass {
            background-color: rgba(31, 31, 35, 0.85);
            border: 1px solid rgba(255, 255, 255, 0.08);
            backdrop-filter: blur(16px);
        }
        .obsidian-card-interactive:hover {
            background-color: rgba(42, 41, 46, 0.88);
            border-color: rgba(0, 242, 254, 0.30);
        }
        .laser-edge-cyan {
            box-shadow: 0 0 12px -2px rgba(0, 242, 254, 0.25), inset 0 0 0 1px rgba(0, 242, 254, 0.55);
        }
        .cyber-btn-primary {
            background-color: rgba(0, 242, 254, 0.14);
            border: 1px solid #00f2fe;
            color: #00f2fe;
            font-weight: 600;
            transition: all 0.15s ease;
        }
        .cyber-btn-primary:hover {
            background-color: #00f2fe;
            color: #002022;
            box-shadow: 0 0 14px rgba(0, 242, 254, 0.45);
        }
        .cyber-btn-solid {
            background-color: #00f2fe;
            color: #002022;
            font-weight: 600;
            box-shadow: 0 0 12px rgba(0, 242, 254, 0.35);
            transition: all 0.15s ease;
        }
        .cyber-btn-solid:hover {
            box-shadow: 0 0 18px rgba(0, 242, 254, 0.60);
            filter: brightness(1.06);
        }
        .cyber-btn-secondary {
            background-color: rgba(255, 255, 255, 0.04);
            border: 1px solid rgba(255, 255, 255, 0.10);
            color: #e4e1e7;
            transition: all 0.15s ease;
        }
        .cyber-btn-secondary:hover {
            background-color: #2a292e;
            border-color: rgba(255, 255, 255, 0.22);
        }
        .cyber-btn-danger {
            background-color: rgba(239, 68, 68, 0.12);
            border: 1px solid rgba(239, 68, 68, 0.40);
            color: #ffb4ab;
            transition: all 0.15s ease;
        }
        .cyber-btn-danger:hover {
            background-color: #93000a;
            color: #ffdad6;
        }

        /* Inputs & Tables */
        .obsidian-input {
            background-color: #0e0e12;
            border: 1px solid rgba(255, 255, 255, 0.10);
            color: #e4e1e7;
            font-family: var(--font-mono);
            font-size: 11px;
            padding: 0.375rem 0.625rem;
            border-radius: 0.25rem;
            width: 100%;
        }
        textarea.obsidian-input {
            line-height: 1.5;
            resize: vertical;
        }
        .obsidian-input:focus {
            outline: none;
            border-color: #00f2fe;
            box-shadow: 0 0 0 1px rgba(0, 242, 254, 0.35);
        }
        table { width: 100%; border-collapse: collapse; }
        tbody tr { border-bottom: 1px solid rgba(255, 255, 255, 0.04); }
        tbody tr:last-child { border-bottom-width: 0; }
        .row-selected {
            background-color: rgba(42, 41, 46, 0.65);
            box-shadow: inset 2px 0 0 0 #00f2fe;
        }

        /* Utilities */
        .truncate { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .whitespace-nowrap { white-space: nowrap; }
        .whitespace-pre { white-space: pre; }
        .overflow-hidden { overflow: hidden; }
        .overflow-x-auto { overflow-x: auto; }
        .overflow-y-auto { overflow-y: auto; }
        .select-none { user-select: none; }
        .select-text { user-select: text; }
        .pointer-events-none { pointer-events: none; }
        .pointer-events-auto { pointer-events: auto; }
        .cursor-pointer { cursor: pointer; }
        .text-left { text-align: left; }
        .text-right { text-align: right; }
        .text-center { text-align: center; }
        .font-medium { font-weight: 500; }
        .font-semibold { font-weight: 600; }
        .font-bold { font-weight: 700; }
        .uppercase { text-transform: uppercase; }
        .tracking-tight { letter-spacing: -0.015em; }
        .tracking-wide { letter-spacing: 0.025em; }
        .tracking-wider { letter-spacing: 0.06em; }
        .opacity-75 { opacity: 0.75; }
        .shadow-md { box-shadow: 0 4px 12px rgba(0,0,0,0.45); }
        .shadow-lg { box-shadow: 0 8px 24px rgba(0,0,0,0.55); }
        .shadow-xl { box-shadow: 0 12px 32px rgba(0,0,0,0.65); }
        .shadow-2xl { box-shadow: 0 16px 48px rgba(0,0,0,0.80); }
        .dot-emerald { width: 6px; height: 6px; border-radius: 9999px; background-color: #67f4b7; box-shadow: 0 0 6px #67f4b7; display: inline-block; }
        .dot-cyan { width: 6px; height: 6px; border-radius: 9999px; background-color: #00f2fe; box-shadow: 0 0 6px #00f2fe; display: inline-block; }
        .dot-violet { width: 6px; height: 6px; border-radius: 9999px; background-color: #dcb8ff; box-shadow: 0 0 6px #dcb8ff; display: inline-block; }
        .dot-crimson { width: 6px; height: 6px; border-radius: 9999px; background-color: #ffb4ab; box-shadow: 0 0 6px #ffb4ab; display: inline-block; }
        .svg-icon { width: 16px; height: 16px; display: inline-block; vertical-align: middle; flex-shrink: 0; }
        .svg-icon-sm { width: 13px; height: 13px; display: inline-block; vertical-align: middle; flex-shrink: 0; }
        .svg-icon-lg { width: 20px; height: 20px; display: inline-block; vertical-align: middle; flex-shrink: 0; }
        @keyframes pulse-glow { 0%, 100% { opacity: 1; } 50% { opacity: 0.45; } }
        .animate-pulse { animation: pulse-glow 2s cubic-bezier(0.4, 0, 0.6, 1) infinite; }
        """;
}
