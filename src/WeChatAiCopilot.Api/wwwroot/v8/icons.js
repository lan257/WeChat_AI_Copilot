export const svg = (name) => {
  const nodes = {
   sparkles:'<path d="m12 3 1.9 5.8L20 11l-6.1 2.2L12 19l-1.9-5.8L4 11l6.1-2.2L12 3Z"/><path d="m20 17 .8 1.5L22 19l-1.2.5L20 21l-.7-1.5L18 19l1.3-.5L20 17ZM4 2l.7 1.3L6 4l-1.3.7L4 6l-.7-1.3L2 4l1.3-.7L4 2Z"/>',
   users:'<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/>',
   settings:'<path d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06-2.87 2.87-.06-.06A1.7 1.7 0 0 0 15 19.4a1.7 1.7 0 0 0-1.2 1.6v.1H9.74V21a1.7 1.7 0 0 0-1.2-1.6 1.7 1.7 0 0 0-1.87.34l-.06.06-2.87-2.87.06-.06A1.7 1.7 0 0 0 4.14 15a1.7 1.7 0 0 0-1.6-1.2h-.1V9.74h.1a1.7 1.7 0 0 0 1.6-1.2 1.7 1.7 0 0 0-.34-1.87l-.06-.06 2.87-2.87.06.06A1.7 1.7 0 0 0 8.54 4.14a1.7 1.7 0 0 0 1.2-1.6v-.1h4.06v.1a1.7 1.7 0 0 0 1.2 1.6 1.7 1.7 0 0 0 1.87-.34l.06-.06 2.87 2.87-.06.06A1.7 1.7 0 0 0 19.4 8.54a1.7 1.7 0 0 0 1.6 1.2h.1v4.06H21a1.7 1.7 0 0 0-1.6 1.2Z" transform="translate(0 0) scale(.96)"/>',
   book:'<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/>',
   minimize:'<path d="m8 3 0 5H3M16 21v-5h5M3 8l5-5M16 16l5 5"/>',maximize:'<path d="M8 3H3v5M3 3l7 7M16 21h5v-5M21 21l-7-7"/>',
   search:'<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
   'chevron-down':'<path d="m6 9 6 6 6-6"/>',
   'more-horizontal':'<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
   'arrow-right':'<path d="M5 12h14m-7-7 7 7-7 7"/>',
   smile:'<circle cx="12" cy="12" r="10"/><path d="M8 14s1.5 2 4 2 4-2 4-2M9 9h.01M15 9h.01"/>',
   refresh:'<path d="M20 11a8 8 0 0 0-14-5L4 8V3M4 8h5M4 13a8 8 0 0 0 14 5l2-2v5m0-5h-5"/>',
   send:'<path d="m22 2-7 20-4-9-9-4 20-7Z"/><path d="M22 2 11 13"/>',
   x:'<path d="M18 6 6 18M6 6l12 12"/>',
   menu:'<path d="M4 7h16M4 12h16M4 17h16"/>',
   info:'<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
   zap:'<path d="m13 2-9 12h7l-1 8 10-12h-7l0-8Z"/>',
   radio:'<circle cx="12" cy="12" r="2"/><path d="M16.24 7.76a6 6 0 0 1 0 8.48m-8.48 0a6 6 0 0 1 0-8.48M19.07 4.93a10 10 0 0 1 0 14.14m-14.14 0a10 10 0 0 1 0-14.14"/>',
   message:'<path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8A8.5 8.5 0 0 1 12.5 20a8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8A8.5 8.5 0 0 1 12.5 3H13a8.5 8.5 0 0 1 8 8v.5Z"/>',
   home:'<path d="m3 10 9-7 9 7v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V10Z"/><path d="M9 21v-8h6v8"/>',
   history:'<path d="M3 12a9 9 0 1 0 3-6.7L3 8m0-5v5h5"/><path d="M12 7v5l3 2"/>',
   sliders:'<path d="M4 7h16M4 17h16"/><circle cx="8" cy="7" r="2" fill="white"/><circle cx="16" cy="17" r="2" fill="white"/>',
   copy:'<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
   check:'<path d="m5 12 4 4L19 6"/>',
   clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>'
  };
  return `<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${nodes[name] || nodes.info}</svg>`;
 };

export function hydrateIcons(root=document){root.querySelectorAll('[data-icon]').forEach(el=>el.innerHTML=svg(el.dataset.icon));}