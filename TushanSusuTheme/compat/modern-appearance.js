(async () => {
  const key = '__susuModernAppearanceCompatibility';
  const version = 7;
  const previous = window[key];
  if (previous?.version === version) {
    return true;
  }
  // Prefer the shell's own surface marker, with both legacy class names as fallbacks.
  const mainSelector = 'main:is([data-app-shell-main-surface],[class*="_MainContentSurface_"],[class^="MainContentSurface-"],[class*=" MainContentSurface-"])';
  const sidebarSelector = 'aside.app-shell-left-panel';
  const composerSelector = ':is([class*="_ComposerLayoutRoot_"],[class^="ComposerLayoutRoot-"],[class*=" ComposerLayoutRoot-"],[data-composer-body])';
  // Mini and auxiliary windows must keep their own appearance.
  if (!document.querySelector(mainSelector) || !document.querySelector(sidebarSelector)) return false;
  const asset = window.__susuModernAppearanceAsset;
  if (typeof asset !== 'string' || !/^data:image\/png;base64,[A-Za-z0-9+/]+=*$/.test(asset)) {
    throw new Error('The local Susu background asset is missing or invalid');
  }
  previous?.restore();

  // Chromium limits custom-property values. Keep the multi-megabyte local image
  // in a Blob, so CSS only receives a short URL instead of a huge data value.
  const raw = atob(asset.slice(asset.indexOf(',') + 1));
  const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  const objectUrl = URL.createObjectURL(new Blob([bytes], {type:'image/png'}));
  const image = new Image();
  image.src = objectUrl;
  try { await image.decode(); }
  catch(error) { URL.revokeObjectURL(objectUrl);throw error; }
  if (!image.naturalWidth || !image.naturalHeight) {URL.revokeObjectURL(objectUrl);throw new Error('The Susu background could not be decoded');}
  delete window.__susuModernAppearanceAsset;

  const panel = '#fff8f2', panelSecondary = '#f6e9e6', panelTertiary = '#eddddf';
  const text = '#382c36', secondary = '#69535e', muted = '#715462';
  const accent = '#a84366', border = '#b998a6';
  const tokens = new Map();
  const set = (names, value) => names.trim().split(/\s+/).forEach(name => tokens.set(name, value));
  // This Codex release uses native semantic variables in addition to the older
  // bg-token-* classes handled by the upstream adapter. Override the variables
  // themselves; changing color-scheme alone leaves the saved dark palette active.
  set(`--codex-base-surface --color-surface --color-surface-card --color-surface-canvas
    --app-color-background-surface --app-color-background-card
    --color-token-main-surface-primary --chat-background-color
    --color-background-composer-surface --composer-background-color
    --menu-background-color --app-color-background-elevated-primary-opaque`, panel);
  set(`--color-surface-secondary --color-surface-recovery --color-token-bg-primary
    --app-color-background-surface-under --app-color-background-shell
    --app-color-background-application-menu --app-color-background-editor-opaque
    --app-color-background-elevated-secondary-opaque --color-background-activity-input
    --color-background-control-opaque --color-background-viewer-control
    --color-background-composer-action-bar --color-token-side-bar-background
    --codeblock-background-color --color-token-text-code-block-background
    --color-token-bg-secondary --color-token-bg-tertiary`, panelSecondary);
  set(`--color-surface-tertiary --color-surface-elevated-secondary
    --color-token-dropdown-background --app-color-background-elevated-tertiary-opaque
    --segmented-control-background --segmented-control-thumb-background
    --segmented-control-option-highlight-background-color
    --color-background-message-reaction --color-background-activity-control`, panelTertiary);
  set('--color-surface-elevated --app-color-background-elevated-primary --app-color-background-control', panel);
  set('--app-color-background-elevated-secondary --input-soft-background-color --color-token-diff-surface', panelSecondary);
  set(`--color-text-primary --color-token-text-primary --app-color-text-foreground
    --color-text-primary-surface --color-text-primary-soft --color-text-primary-soft-alt
    --color-text-primary-ghost --color-text-primary-ghost-hover
    --color-text-primary-outline --color-text-primary-outline-hover
    --user-message-text-color --input-text-color --color-text-composer-primary
    --app-color-text-primary-solid --color-text-chatgpt-recovery`, text);
  set(`--color-text-secondary --color-token-text-secondary --color-text-secondary-solid
    --app-color-text-secondary --app-color-text-foreground-secondary
    --color-text-secondary-outline --color-text-secondary-outline-hover
    --color-text-secondary-soft --color-text-secondary-soft-alt
    --color-text-secondary-ghost --color-text-secondary-ghost-hover
    --app-color-text-button-secondary --color-text-activity-control`, secondary);
  set(`--color-text-tertiary --color-token-text-tertiary --app-color-text-foreground-tertiary
    --input-placeholder-text-color --app-color-text-button-tertiary --color-text-disabled`, muted);
  set(`--codex-base-accent --app-color-accent-purple --app-color-accent-blue
    --app-color-text-accent --app-color-icon-accent --color-token-text-link-foreground
    --color-text-composer-reference --link-primary-text-color
    --color-background-primary-solid --app-color-background-button-primary`, accent);
  set('--app-color-text-button-primary --app-color-text-on-accent --color-text-primary-solid --color-text-accent-action --color-text-inverse', panel);
  set('--app-color-background-button-primary-hover --color-background-primary-solid-hover', '#923753');
  set('--app-color-background-button-primary-active --color-background-primary-solid-active', '#7b2d46');
  set(`--app-color-background-button-primary-inactive --app-color-background-button-secondary
    --app-color-background-button-secondary-inactive --app-color-background-button-tertiary
    --color-token-list-hover-background --color-token-interactive-bg-secondary-hover
    --menu-item-background-color --user-message-background-color
    --app-color-background-accent --app-color-background-accent-hover
    --app-color-background-accent-active`, '#efdee5');
  set(`--app-color-background-button-secondary-hover --app-color-background-button-secondary-active
    --app-color-background-button-tertiary-hover --app-color-background-button-tertiary-active
    --color-background-primary-soft-hover --color-background-primary-soft-active
    --color-background-primary-soft-alpha --color-background-primary-outline-hover
    --color-background-primary-ghost-focus --color-background-secondary-soft-hover
    --color-background-secondary-soft-alpha-active`, '#e5cbd6');
  set(`--color-border --color-border-strong --color-border-subtle --color-border-overlay
    --color-border-disabled --color-border-tooltip --color-border-button-outline
    --color-border-primary-outline --color-border-primary-outline-hover
    --color-border-primary-surface --color-border-primary-soft-alt
    --color-border-secondary-outline --color-border-secondary-outline-hover
    --color-border-secondary-soft-alt --color-border-mode-toggle-selected
    --color-token-border --color-token-border-default --color-token-border-heavy
    --color-token-border-light --color-token-input-border --color-token-interactive-border-focus
    --input-outline-border-color --input-outline-border-color-hover
    --input-soft-border-color-focus --menu-separator-background-color
    --app-color-border-application-menu-separator`, border);
  set('--color-token-focus-border --input-outline-border-color-focus', accent);
  set('--color-token-scrollbar-slider-background --color-token-scrollbar-slider-hover-background --color-token-scrollbar-slider-active-background', '#b998a680');
  // Native tooltips read these tokens even when their visible surface has no tooltip role.
  set('--color-background-tooltip', panel);
  set('--color-text-tooltip', text);
  set('--tooltip-text-info-color --tooltip-text-warning-color --tooltip-text-success-color', secondary);

  const style = document.createElement('style');
  style.setAttribute('data-susu-modern-appearance', 'true');
  const scope = 'html[data-susu-runtime-appearance]';
  const declarations = [...tokens].map(([name,value]) => `${name}:${value}!important;`).join('');
  // Some native routes have their own theme boundary. Reapply the same semantic
  // palette there rather than rewriting component classes or stored preferences.
  const paletteScopes = `${scope},${scope} [data-theme],${scope} [data-color-scheme],${scope} .light,${scope} .dark`;
  const mainSurface = `${scope} ${mainSelector}`;
  const sidebarSurface = `${scope} ${sidebarSelector}`;
  const composerSurface = `${scope} :is(${composerSelector})`;
  style.textContent = `
    ${paletteScopes}{${declarations}color-scheme:light!important;}
    ${scope}{--susu-runtime-image:url("${objectUrl}");background:${panel}!important;color:${text}!important;}
    ${scope} body{background:${panel}!important;color:${text}!important;}
    ${scope} body::before,${scope} body::after{content:none!important;}
    ${mainSurface}{
      background-color:${panel}!important;
      background-image:linear-gradient(rgba(255,248,242,.86),rgba(255,248,242,.86)),var(--susu-runtime-image)!important;
      background-position:center center,34% 50%!important;
      background-size:cover!important;background-repeat:no-repeat!important;background-attachment:scroll!important;
      color:${text}!important;
    }
    ${sidebarSurface}{
      background-color:${panel}!important;
      background-image:linear-gradient(rgba(255,248,242,.60),rgba(255,248,242,.60)),var(--susu-runtime-image)!important;
      background-position:center center,34% 50%!important;
      background-size:cover!important;background-repeat:no-repeat!important;background-attachment:scroll!important;
      color:#33252f!important;
    }
    ${sidebarSurface} .sidebar-navigation{background-color:transparent!important;}
    /* The profile route has an opaque backdrop over the shell artwork. */
    ${mainSurface} .bg-surface:has(> [data-profile-scroll]){background-color:transparent!important;}
    ${composerSurface}{background:${panel}!important;color:${text}!important;border-color:${border}!important;}
    /* Pair tooltip text and background. The positioning wrapper stays transparent. */
    ${scope} [role="tooltip"]{background-color:${panel}!important;color:${text}!important;border-color:${border}!important;}
    ${scope} :is([role="menu"],[role="dialog"],[role="alertdialog"],[role="listbox"],
      [data-radix-popper-content-wrapper]:not(:has([role="tooltip"]))){background-color:${panel}!important;color:${text}!important;}
    ${scope} :is([role="menu"],[role="dialog"],[role="alertdialog"],[role="listbox"]) :is(input,textarea,[contenteditable="true"]){background:${panel}!important;color:${text}!important;}
    ${mainSurface},${sidebarSurface},${composerSurface},
    ${scope} :is([role="menu"],[role="dialog"],[role="alertdialog"],[role="listbox"],[class*="backdrop-blur"]){backdrop-filter:none!important;-webkit-backdrop-filter:none!important;}
  `;

  let active = true;
  const stats = {version,observerCount:0,timerCount:0,scrollListenerCount:0,refreshes:0,verifications:0,imageWidth:image.naturalWidth,imageHeight:image.naturalHeight,restored:false};
  const visible = node => {
    if (!node?.isConnected) return false;
    const rect = node.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return false;
    const computed = getComputedStyle(node);
    return computed.display !== 'none' && computed.visibility !== 'hidden' && computed.visibility !== 'collapse' && Number(computed.opacity) !== 0 && !node.closest('[hidden],[aria-hidden="true"]');
  };
  // Route changes and streamed text are handled by CSS. There is deliberately no
  // DOM observer, scroll/resize listener, timer, or recurring layout scan.
  const refresh = () => active;
  const previousRootAttribute = document.documentElement.getAttribute('data-susu-runtime-appearance');
  document.documentElement.setAttribute('data-susu-runtime-appearance', 'true');
  document.head.append(style);

  const colorCanvas = document.createElement('canvas');colorCanvas.width = colorCanvas.height = 1;
  const colorContext = colorCanvas.getContext('2d', {willReadFrequently:true});
  const rgb = color => {
    colorContext.clearRect(0,0,1,1);colorContext.fillStyle = color;colorContext.fillRect(0,0,1,1);
    return [...colorContext.getImageData(0,0,1,1).data];
  };
  const luminance = value => value.slice(0,3).map(channel => channel / 255).map(channel => channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4).reduce((sum,value,index) => sum + value * [.2126,.7152,.0722][index],0);
  const contrast = (foreground,background) => {const a=luminance(rgb(foreground)), b=luminance(rgb(background));return (Math.max(a,b)+.05)/(Math.min(a,b)+.05)};
  const verify = () => {
    stats.verifications++;
    const main = [...document.querySelectorAll(mainSelector)].find(visible) || document.querySelector(mainSelector);
    const sidebar = [...document.querySelectorAll(sidebarSelector)].find(visible) || document.querySelector(sidebarSelector);
    const paragraph = main?.querySelector('p,[class*="_Paragraph_"],[class^="Paragraph-"],[class*=" Paragraph-"],[class*="_MarkdownRoot_"],[class^="MarkdownRoot-"],[class*=" MarkdownRoot-"]');
    const editor = [...(main?.querySelectorAll('.ProseMirror[contenteditable="true"],[role="textbox"][contenteditable="true"],textarea') || [])].find(visible);
    const composer = editor?.closest(composerSelector);
    const mainStyle = main && getComputedStyle(main), editorStyle = editor && getComputedStyle(editor);
    const rootStyle = getComputedStyle(document.documentElement);
    const settingsVisible = [...document.querySelectorAll('[data-settings-panel-slug],[data-settings-mobile-header],[data-settings-page]')].some(visible);
    const pageKind = /(?:^|\/)(?:settings|preferences)(?:\/|$)/i.test(location.pathname + location.hash) || settingsVisible ? 'settings' : editor ? 'chat' : 'page';
    // The image can be dark at an arbitrary pixel. This conservative value is
    // the 86% cream reading layer over a completely black image pixel.
    const darkestReadingSurface = 'rgb(219,213,208)';
    const bodyColor = getComputedStyle(visible(paragraph) ? paragraph : main || document.body).color;
    const composerColor = editorStyle?.color || null;
    const bodyContrast = contrast(bodyColor, darkestReadingSurface);
    const sidebarColor = sidebar && getComputedStyle(sidebar).color;
    const sidebarContrast = sidebarColor ? contrast(sidebarColor, 'rgb(153,149,145)') : 0;
    const composerContrast = composerColor && composer ? contrast(composerColor, getComputedStyle(composer).backgroundColor) : 0;
    const rootBackground = rootStyle.backgroundColor;
    const paletteApplied = active && style.isConnected && rootStyle.getPropertyValue('--app-color-background-surface').trim().toLowerCase() === panel && getComputedStyle(main || document.body).getPropertyValue('--app-color-text-foreground').trim().toLowerCase() === text;
    const sidebarBackgroundImage = Boolean(sidebar && getComputedStyle(sidebar).backgroundImage.includes(objectUrl));
    const result = {
      version,pageKind,paletteApplied,imageDecoded: image.complete && image.naturalWidth > 0,
      mainVisible: visible(main), mainBackgroundImage: Boolean(mainStyle?.backgroundImage.includes(objectUrl)),
      mainBackgroundColor:mainStyle?.backgroundColor || null,
      rootBackground, rootLight:rgb(rootBackground).slice(0,3).every(value => value > 220),
      bodyColor, bodyContrast:Number(bodyContrast.toFixed(2)),
      sidebarVisible:visible(sidebar),sidebarBackgroundImage,sidebarColor,sidebarContrast:Number(sidebarContrast.toFixed(2)),
      composerVisible:visible(composer) && visible(editor), composerColor,
      composerBackground:composer ? getComputedStyle(composer).backgroundColor : null,
      composerContrast:Number(composerContrast.toFixed(2)),
      horizontalOverflow:document.documentElement.scrollWidth > document.documentElement.clientWidth,
      stats:{...stats}
    };
    result.valid = active && result.paletteApplied && result.imageDecoded && result.mainVisible && result.mainBackgroundImage && result.sidebarBackgroundImage && result.rootLight && bodyContrast >= 4.5 && (!result.sidebarVisible || sidebarContrast >= 4.5) && (!result.composerVisible || composerContrast >= 4.5) && !result.horizontalOverflow;
    return result;
  };
  window[key] = {version,refresh,verify,stats,restore:() => {
    if (!active) return true;
    active = false;stats.restored = true;
    if (previousRootAttribute === null) document.documentElement.removeAttribute('data-susu-runtime-appearance');
    else document.documentElement.setAttribute('data-susu-runtime-appearance',previousRootAttribute);
    style.remove();URL.revokeObjectURL(objectUrl);image.src='';colorCanvas.width=colorCanvas.height=0;delete window[key];return true;
  }};
  return true;
})()
