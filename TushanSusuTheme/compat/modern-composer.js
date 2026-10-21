(() => {
  const key='__susuModernComposerCompatibility';
  const previous=window[key];
  if(previous?.compatVersion===4&&previous.boundMarker===window.__openChatgptSkinMarkSurfaces){previous.mark();return true}
  if(previous){
    if(typeof previous.restore==='function')previous.restore();
    else {previous.observer?.disconnect();window.removeEventListener('resize',previous.mark);window.removeEventListener('scroll',previous.mark,true);previous.style?.remove();delete window[key]}
  }
  let active=true;
  const wrappers=[];
  const visible=node=>{const r=node.getBoundingClientRect(),s=getComputedStyle(node);return r.width>0&&r.height>0&&s.display!=='none'&&s.visibility!=='hidden'&&s.visibility!=='collapse'};
  const mark=()=>{
    if(!active)return;
    const main=[...document.querySelectorAll('main[class*="_MainContentSurface_"]')].filter(visible).sort((a,b)=>b.getBoundingClientRect().width*b.getBoundingClientRect().height-a.getBoundingClientRect().width*a.getBoundingClientRect().height)[0];
    if(main){for(const node of document.querySelectorAll('[data-open-chatgpt-skin-surface="main"]'))if(node!==main)node.removeAttribute('data-open-chatgpt-skin-surface');main.setAttribute('data-open-chatgpt-skin-surface','main')}
    const sidebar=[...document.querySelectorAll('aside.app-shell-left-panel')].find(visible);
    if(sidebar){for(const node of document.querySelectorAll('[data-open-chatgpt-skin-surface="sidebar"]'))if(node!==sidebar)node.removeAttribute('data-open-chatgpt-skin-surface');sidebar.setAttribute('data-open-chatgpt-skin-surface','sidebar')}
    for(const editor of document.querySelectorAll('.ProseMirror[contenteditable="true"],[role="textbox"][contenteditable="true"],textarea')){
      if(!visible(editor)||editor.closest('.xterm,[role="terminal"],[role="menu"],[role="dialog"]'))continue;
      const composer=editor.closest('[class*="_ComposerLayoutRoot_"]')||editor.closest('[data-composer-body]')||editor.closest('form');
      if(!composer||!visible(composer))continue;
      composer.setAttribute('data-open-chatgpt-skin-surface','composer');
      editor.setAttribute('data-open-chatgpt-skin-surface','composer-input');
    }
  };
  const originalKeys=Object.getOwnPropertyNames(window).filter(name=>/skin/i.test(name)&&typeof window[name]==='function'&&!name.includes('__susu'));
  for(const name of originalKeys){const original=window[name];if(original.name!=='markSurfaces')continue;const wrapped=function(...args){const result=original.apply(this,args);mark();return result};window[name]=wrapped;wrappers.push({name,original,wrapped})}
  const observer=new MutationObserver(()=>queueMicrotask(mark));observer.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['class','hidden','aria-hidden']});
  window.addEventListener('resize',mark);
  window.addEventListener('scroll',mark,true);
  const style=document.createElement('style');style.setAttribute('data-susu-modern-composer','true');
  style.textContent='[class*="_ComposerLayoutRoot_"][data-open-chatgpt-skin-surface="composer"]{width:100%!important;max-width:100%!important;}';
  document.head.append(style);
  window[key]={compatVersion:4,boundMarker:window.__openChatgptSkinMarkSurfaces,mark,observer,style,restore:()=>{active=false;observer.disconnect();window.removeEventListener('resize',mark);window.removeEventListener('scroll',mark,true);for(const item of wrappers)if(window[item.name]===item.wrapped)window[item.name]=item.original;style.remove();delete window[key];return true}};mark();return true;
})()
